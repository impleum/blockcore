﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Blockcore.Configuration;
using Blockcore.Interfaces;
using Blockcore.Utilities;
using DBreeze.Utils;
using RocksDbSharp;
using Microsoft.Extensions.Logging;
using NBitcoin;
using System;
using Blockcore.Consensus.BlockInfo;
using Blockcore.Consensus.Chain;
using Blockcore.Consensus.TransactionInfo;
using Blockcore.Networks;

namespace Blockcore.Features.BlockStore
{
    /// <summary>
    /// <see cref="IBlockRepository"/> is the interface to all the logics interacting with the blocks stored in the database.
    /// </summary>
    public interface IBlockRepository : IBlockStore
    {
        /// <summary> The dbreeze database engine.</summary>
        RocksDb RocksDb { get; }

        /// <summary>Really ugly temporary hack.</summary>
        object Locker { get; }

        /// <summary>
        /// Deletes blocks and indexes for transactions that belong to deleted blocks.
        /// <para>
        /// It should be noted that this does not delete the entries from disk (only the references are removed) and
        /// as such the file size remains the same.
        /// </para>
        /// </summary>
        /// <remarks>TODO: This will need to be revisited once DBreeze has been fixed or replaced with a solution that works.</remarks>
        /// <param name="hashes">List of block hashes to be deleted.</param>
        void DeleteBlocks(List<uint256> hashes);

        /// <summary>
        /// Persist the next block hash and insert new blocks into the database.
        /// </summary>
        /// <param name="newTip">Hash and height of the new repository's tip.</param>
        /// <param name="blocks">Blocks to be inserted.</param>
        void PutBlocks(HashHeightPair newTip, List<Block> blocks);

        /// <summary>
        /// Wipe out blocks and their transactions then replace with a new block.
        /// </summary>
        /// <param name="newTip">Hash and height of the new repository's tip.</param>
        /// <param name="hashes">List of all block hashes to be deleted.</param>
        /// <exception cref="DBreezeException">Thrown if an error occurs during database operations.</exception>
        void Delete(HashHeightPair newTip, List<uint256> hashes);

        /// <summary>
        /// Determine if a block already exists
        /// </summary>
        /// <param name="hash">The hash.</param>
        /// <returns><c>true</c> if the block hash can be found in the database, otherwise return <c>false</c>.</returns>
        bool Exist(uint256 hash);

        /// <summary>
        /// Iterate over every block in the database.
        /// If <see cref="TxIndex"/> is true, we store the block hash alongside the transaction hash in the transaction table, otherwise clear the transaction table.
        /// </summary>
        void ReIndex();

        /// <summary>
        /// Set whether to index transactions by block hash, as well as storing them inside of the block.
        /// </summary>
        /// <param name="txIndex">Whether to index transactions.</param>
        void SetTxIndex(bool txIndex);

        /// <summary>Hash and height of the repository's tip.</summary>
        HashHeightPair TipHashAndHeight { get; }

        /// <summary> Indicates that the node should store all transaction data in the database.</summary>
        bool TxIndex { get; }
    }

    public class BlockRepository : IBlockRepository
    {
        internal static readonly byte BlockTableName = 1;
        internal static readonly byte CommonTableName = 2;
        internal static readonly byte TransactionTableName = 3;

        private readonly RocksDb rocksdb;

        public object Locker { get; }

        private readonly ILogger logger;

        private readonly Network network;

        private static readonly byte[] RepositoryTipKey = new byte[0];

        private static readonly byte[] TxIndexKey = new byte[1];

        /// <inheritdoc />
        public HashHeightPair TipHashAndHeight { get; private set; }

        /// <inheritdoc />
        public bool TxIndex { get; private set; }

        public RocksDb RocksDb => this.rocksdb;

        private readonly DataStoreSerializer dataStoreSerializer;
        private readonly IReadOnlyDictionary<uint256, Transaction> genesisTransactions;

        public BlockRepository(Network network, DataFolder dataFolder,
            ILoggerFactory loggerFactory, DataStoreSerializer dataStoreSerializer)
            : this(network, dataFolder.BlockPath, loggerFactory, dataStoreSerializer)
        {
        }

        public BlockRepository(Network network, string folder, ILoggerFactory loggerFactory, DataStoreSerializer dataStoreSerializer)
        {
            Guard.NotNull(network, nameof(network));
            Guard.NotEmpty(folder, nameof(folder));

            Directory.CreateDirectory(folder);
            var options = new DbOptions().SetCreateIfMissing(true);
            this.rocksdb = RocksDb.Open(options, folder);
            this.Locker = new object();

            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);
            this.network = network;
            this.dataStoreSerializer = dataStoreSerializer;
            this.genesisTransactions = network.GetGenesis().Transactions.ToDictionary(k => k.GetHash());
        }

        /// <inheritdoc />
        public virtual void Initialize()
        {
            Block genesis = this.network.GetGenesis();

            lock (this.Locker)
            {
                if (this.LoadTipHashAndHeight() == null)
                {
                    this.SaveTipHashAndHeight(new HashHeightPair(genesis.GetHash(), 0));
                }

                if (this.LoadTxIndex() == null)
                {
                    this.SaveTxIndex(false);
                }
            }
        }

        /// <inheritdoc />
        public Transaction GetTransactionById(uint256 trxid)
        {
            Guard.NotNull(trxid, nameof(trxid));

            if (!this.TxIndex)
            {
                this.logger.LogTrace("(-)[TX_INDEXING_DISABLED]:null");
                return default(Transaction);
            }

            if (this.genesisTransactions.TryGetValue(trxid, out Transaction genesisTransaction))
            {
                return genesisTransaction;
            }

            Transaction res = null;
            lock (this.Locker)
            {
                byte[] transactionRow = this.rocksdb.Get(DBH.Key(TransactionTableName, trxid.ToBytes()));

                if (transactionRow == null)
                {
                    this.logger.LogTrace("(-)[NO_BLOCK]:null");
                    return null;
                }

                byte[] blockRow = this.rocksdb.Get(DBH.Key(BlockTableName, transactionRow));

                if (blockRow != null)
                {
                    var block = this.dataStoreSerializer.Deserialize<Block>(blockRow);
                    res = block.Transactions.FirstOrDefault(t => t.GetHash() == trxid);
                }
            }

            return res;
        }

        /// <inheritdoc/>
        public Transaction[] GetTransactionsByIds(uint256[] trxids, CancellationToken cancellation = default(CancellationToken))
        {
            if (!this.TxIndex)
            {
                this.logger.LogTrace("(-)[TX_INDEXING_DISABLED]:null");
                return null;
            }

            Transaction[] txes = new Transaction[trxids.Length];

            lock (this.Locker)
            {
                for (int i = 0; i < trxids.Length; i++)
                {
                    cancellation.ThrowIfCancellationRequested();

                    bool alreadyFetched = trxids.Take(i).Any(x => x == trxids[i]);

                    if (alreadyFetched)
                    {
                        this.logger.LogDebug("Duplicated transaction encountered. Tx id: '{0}'.", trxids[i]);

                        txes[i] = txes.First(x => x.GetHash() == trxids[i]);
                        continue;
                    }

                    if (this.genesisTransactions.TryGetValue(trxids[i], out Transaction genesisTransaction))
                    {
                        txes[i] = genesisTransaction;
                        continue;
                    }

                    byte[] transactionRow = this.rocksdb.Get(DBH.Key(TransactionTableName, trxids[i].ToBytes()));
                    if (transactionRow == null)
                    {
                        this.logger.LogTrace("(-)[NO_TX_ROW]:null");
                        return null;
                    }

                    byte[] blockRow = this.rocksdb.Get(DBH.Key(BlockTableName, transactionRow));

                    if (blockRow != null)
                    {
                        this.logger.LogTrace("(-)[NO_BLOCK]:null");
                        return null;
                    }

                    var block = this.dataStoreSerializer.Deserialize<Block>(blockRow);
                    Transaction tx = block.Transactions.FirstOrDefault(t => t.GetHash() == trxids[i]);

                    txes[i] = tx;
                }
            }

            return txes;
        }

        /// <inheritdoc />
        public uint256 GetBlockIdByTransactionId(uint256 trxid)
        {
            Guard.NotNull(trxid, nameof(trxid));

            if (!this.TxIndex)
            {
                this.logger.LogTrace("(-)[NO_TXINDEX]:null");
                return default(uint256);
            }

            if (this.genesisTransactions.ContainsKey(trxid))
            {
                return this.network.GenesisHash;
            }

            uint256 res = null;
            lock (this.Locker)
            {
                byte[] transactionRow = this.rocksdb.Get(DBH.Key(TransactionTableName, trxid.ToBytes()));
                if (transactionRow != null)
                    res = new uint256(transactionRow);
            }

            return res;
        }

        protected virtual void OnInsertBlocks(List<Block> blocks)
        {
            var transactions = new List<(Transaction, Block)>();
            var byteListComparer = new ByteListComparer();
            var blockDict = new Dictionary<uint256, Block>();

            // Gather blocks.
            foreach (Block block in blocks)
            {
                uint256 blockId = block.GetHash();
                blockDict[blockId] = block;
            }

            // Sort blocks. Be consistent in always converting our keys to byte arrays using the ToBytes method.
            List<KeyValuePair<uint256, Block>> blockList = blockDict.ToList();
            blockList.Sort((pair1, pair2) => byteListComparer.Compare(pair1.Key.ToBytes(), pair2.Key.ToBytes()));

            using (var batch = new WriteBatch())
            {
                // Index blocks.
                foreach (KeyValuePair<uint256, Block> kv in blockList)
                {
                    uint256 blockId = kv.Key;
                    Block block = kv.Value;

                    batch.Put(DBH.Key(BlockTableName, blockId.ToBytes()), this.dataStoreSerializer.Serialize(block));

                    if (this.TxIndex)
                    {
                        foreach (Transaction transaction in block.Transactions)
                            transactions.Add((transaction, block));
                    }
                }

                this.rocksdb.Write(batch);
            }

            if (this.TxIndex)
                this.OnInsertTransactions(transactions);
        }

        protected virtual void OnInsertTransactions(List<(Transaction, Block)> transactions)
        {
            var byteListComparer = new ByteListComparer();
            transactions.Sort((pair1, pair2) => byteListComparer.Compare(pair1.Item1.GetHash().ToBytes(), pair2.Item1.GetHash().ToBytes()));

            using (var batch = new WriteBatch())
            {
                // Index transactions.
                foreach ((Transaction transaction, Block block) in transactions)
                    batch.Put(DBH.Key(TransactionTableName, transaction.GetHash().ToBytes()), block.GetHash().ToBytes());

                this.rocksdb.Write(batch);
            }
        }

        public IEnumerable<Block> EnumeratehBatch(List<ChainedHeader> headers)
        {
            lock (this.Locker)
            {
                foreach (ChainedHeader chainedHeader in headers)
                {
                    byte[] blockRow = this.rocksdb.Get(DBH.Key(BlockTableName, chainedHeader.HashBlock.ToBytes()));
                    Block block = blockRow != null ? this.dataStoreSerializer.Deserialize<Block>(blockRow) : null;
                    yield return block;
                }
            }
        }

        /// <inheritdoc />
        public void ReIndex()
        {
            lock (this.Locker)
            {
                if (this.TxIndex)
                {
                    int rowCount = 0;
                    // Insert transactions to database.

                    int totalBlocksCount = this.TipHashAndHeight?.Height ?? 0;

                    var warningMessage = new StringBuilder();
                    warningMessage.AppendLine("".PadRight(59, '=') + " W A R N I N G " + "".PadRight(59, '='));
                    warningMessage.AppendLine();
                    warningMessage.AppendLine($"Starting ReIndex process on a total of {totalBlocksCount} blocks.");
                    warningMessage.AppendLine("The operation could take a long time, please don't stop it.");
                    warningMessage.AppendLine();
                    warningMessage.AppendLine("".PadRight(133, '='));
                    warningMessage.AppendLine();

                    this.logger.LogInformation(warningMessage.ToString());
                    using (var batch = new WriteBatch())
                    {
                        var enumerator = this.rocksdb.NewIterator();
                        for (enumerator.SeekToFirst(); enumerator.Valid(); enumerator.Next())
                        {
                            if (enumerator.Key()[0] == BlockTableName)
                            {
                                var block = this.dataStoreSerializer.Deserialize<Block>(enumerator.Value());
                                foreach (Transaction transaction in block.Transactions)
                                {
                                    batch.Put(DBH.Key(TransactionTableName, transaction.GetHash().ToBytes()), block.GetHash().ToBytes());
                                }

                                // inform the user about the ongoing operation
                                if (++rowCount % 1000 == 0)
                                {
                                    this.logger.LogInformation("Reindex in process... {0}/{1} blocks processed.", rowCount, totalBlocksCount);
                                }
                            }
                        }

                        this.rocksdb.Write(batch);
                    }

                    this.logger.LogInformation("Reindex completed successfully.");
                }
                else
                {
                    var enumerator = this.rocksdb.NewIterator();
                    for (enumerator.SeekToFirst(); enumerator.Valid(); enumerator.Next())
                    {
                        // Clear tx from database.
                        if (enumerator.Key()[0] == TransactionTableName)
                            this.rocksdb.Remove(enumerator.Key());
                    }
                }
            }
        }

        /// <inheritdoc />
        public void PutBlocks(HashHeightPair newTip, List<Block> blocks)
        {
            Guard.NotNull(newTip, nameof(newTip));
            Guard.NotNull(blocks, nameof(blocks));

            // DBreeze is faster if sort ascending by key in memory before insert
            // however we need to find how byte arrays are sorted in DBreeze.
            lock (this.Locker)
            {
                this.OnInsertBlocks(blocks);

                // Commit additions
                this.SaveTipHashAndHeight(newTip);
            }
        }

        private bool? LoadTxIndex()
        {
            bool? res = null;
            byte[] row = this.rocksdb.Get(DBH.Key(CommonTableName, TxIndexKey));
            if (row != null)
            {
                this.TxIndex = BitConverter.ToBoolean(row);
                res = this.TxIndex;
            }

            return res;
        }

        private void SaveTxIndex(bool txIndex)
        {
            this.TxIndex = txIndex;
            this.rocksdb.Put(DBH.Key(CommonTableName, TxIndexKey), BitConverter.GetBytes(txIndex));
        }

        /// <inheritdoc />
        public void SetTxIndex(bool txIndex)
        {
            lock (this.Locker)
            {
                this.SaveTxIndex(txIndex);
            }
        }

        private HashHeightPair LoadTipHashAndHeight()
        {
            if (this.TipHashAndHeight == null)
            {
                byte[] row = this.rocksdb.Get(DBH.Key(CommonTableName, RepositoryTipKey));
                if (row != null)
                    this.TipHashAndHeight = this.dataStoreSerializer.Deserialize<HashHeightPair>(row);
            }

            return this.TipHashAndHeight;
        }

        private void SaveTipHashAndHeight(HashHeightPair newTip)
        {
            this.TipHashAndHeight = newTip;
            this.rocksdb.Put(DBH.Key(CommonTableName, RepositoryTipKey), this.dataStoreSerializer.Serialize(newTip));
        }

        /// <inheritdoc />
        public Block GetBlock(uint256 hash)
        {
            Guard.NotNull(hash, nameof(hash));

            Block res = null;
            lock (this.Locker)
            {
                var results = this.GetBlocksFromHashes(new List<uint256> { hash });

                if (results.FirstOrDefault() != null)
                    res = results.FirstOrDefault();
            }

            return res;
        }

        /// <inheritdoc />
        public List<Block> GetBlocks(List<uint256> hashes)
        {
            Guard.NotNull(hashes, nameof(hashes));

            List<Block> blocks;

            lock (this.Locker)
            {
                blocks = this.GetBlocksFromHashes(hashes);
            }

            return blocks;
        }

        /// <inheritdoc />
        public bool Exist(uint256 hash)
        {
            Guard.NotNull(hash, nameof(hash));

            bool res = false;
            lock (this.Locker)
            {
                // Lazy loading is on so we don't fetch the whole value, just the row.
                byte[] key = hash.ToBytes();
                byte[] blockRow = this.rocksdb.Get(DBH.Key(BlockTableName, key));
                if (blockRow != null)
                    res = true;
            }

            return res;
        }

        protected virtual void OnDeleteTransactions(List<(Transaction, Block)> transactions)
        {
            foreach ((Transaction transaction, Block block) in transactions)
                this.rocksdb.Remove(DBH.Key(TransactionTableName, transaction.GetHash().ToBytes()));
        }

        protected virtual void OnDeleteBlocks(List<Block> blocks)
        {
            if (this.TxIndex)
            {
                var transactions = new List<(Transaction, Block)>();

                foreach (Block block in blocks)
                    foreach (Transaction transaction in block.Transactions)
                        transactions.Add((transaction, block));

                this.OnDeleteTransactions(transactions);
            }

            foreach (Block block in blocks)
                this.rocksdb.Remove(DBH.Key(BlockTableName, block.GetHash().ToBytes()));
        }

        public List<Block> GetBlocksFromHashes(List<uint256> hashes)
        {
            var results = new Dictionary<uint256, Block>();

            // Access hash keys in sorted order.
            var byteListComparer = new ByteListComparer();
            List<(uint256, byte[])> keys = hashes.Select(hash => (hash, hash.ToBytes())).ToList();

            keys.Sort((key1, key2) => byteListComparer.Compare(key1.Item2, key2.Item2));

            foreach ((uint256, byte[]) key in keys)
            {
                // If searching for genesis block, return it.
                if (key.Item1 == this.network.GenesisHash)
                {
                    results[key.Item1] = this.network.GetGenesis();
                    continue;
                }

                byte[] blockRow = this.rocksdb.Get(DBH.Key(BlockTableName, key.Item2));
                if (blockRow != null)
                {
                    results[key.Item1] = this.dataStoreSerializer.Deserialize<Block>(blockRow);

                    this.logger.LogDebug("Block hash '{0}' loaded from the store.", key.Item1);
                }
                else
                {
                    results[key.Item1] = null;

                    this.logger.LogDebug("Block hash '{0}' not found in the store.", key.Item1);
                }
            }

            // Return the result in the order that the hashes were presented.
            return hashes.Select(hash => results[hash]).ToList();
        }

        /// <inheritdoc />
        public void Delete(HashHeightPair newTip, List<uint256> hashes)
        {
            Guard.NotNull(newTip, nameof(newTip));
            Guard.NotNull(hashes, nameof(hashes));

            lock (this.Locker)
            {
                List<Block> blocks = this.GetBlocksFromHashes(hashes);
                this.OnDeleteBlocks(blocks.Where(b => b != null).ToList());
                this.SaveTipHashAndHeight(newTip);
            }
        }

        /// <inheritdoc />
        public void DeleteBlocks(List<uint256> hashes)
        {
            Guard.NotNull(hashes, nameof(hashes));

            lock (this.Locker)
            {
                List<Block> blocks = this.GetBlocksFromHashes(hashes);

                this.OnDeleteBlocks(blocks.Where(b => b != null).ToList());
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            this.rocksdb.Dispose();
        }
    }
}