﻿using System.Collections.Generic;
using System.Threading.Tasks;
using Blockcore.Base;
using Blockcore.Consensus;
using Blockcore.Consensus.Chain;
using Blockcore.Interfaces;
using Blockcore.Tests.Common;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Blockcore.Features.BlockStore.Tests
{
    public class BlockStoreBehaviorTest
    {
        private readonly BlockStoreBehavior behavior;
        private readonly Mock<IChainState> chainState;
        private readonly ChainIndexer chainIndexer;
        private readonly ILoggerFactory loggerFactory;
        private readonly Mock<IConsensusManager> consensusManager;
        private readonly Mock<IBlockStoreQueue> blockStore;

        public BlockStoreBehaviorTest()
        {
            this.loggerFactory = new LoggerFactory();
            this.chainIndexer = new ChainIndexer(KnownNetworks.StratisMain);
            this.chainState = new Mock<IChainState>();
            this.consensusManager = new Mock<IConsensusManager>();
            this.blockStore = new Mock<IBlockStoreQueue>();

            this.behavior = new BlockStoreBehavior(this.chainIndexer, this.chainState.Object, this.loggerFactory, this.consensusManager.Object, this.blockStore.Object);
        }

        [Fact]
        public void AnnounceBlocksWithoutBlocksReturns()
        {
            var blocks = new List<ChainedHeader>();

            Task task = this.behavior.AnnounceBlocksAsync(blocks);

            Assert.Equal(TaskStatus.RanToCompletion, task.Status);
            Assert.Null(this.behavior.AttachedPeer);
        }
    }
}