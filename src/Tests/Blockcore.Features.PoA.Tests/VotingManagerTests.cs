﻿using System.Collections.Generic;
using Blockcore.Consensus.BlockInfo;
using Blockcore.Consensus.Chain;
using Blockcore.Consensus.ScriptInfo;
using Blockcore.Consensus.TransactionInfo;
using Blockcore.EventBus.CoreEvents;
using Blockcore.Features.PoA.Voting;
using Blockcore.Tests.Common;
using Moq;
using NBitcoin;
using Xunit;

namespace Blockcore.Features.PoA.Tests
{
    public class VotingManagerTests : PoATestsBase
    {
        private readonly VotingDataEncoder encoder;

        private readonly List<VotingData> changesApplied;
        private readonly List<VotingData> changesReverted;

        public VotingManagerTests()
        {
            this.encoder = new VotingDataEncoder(this.loggerFactory);
            this.changesApplied = new List<VotingData>();
            this.changesReverted = new List<VotingData>();

            this.resultExecutorMock.Setup(x => x.ApplyChange(It.IsAny<VotingData>())).Callback((VotingData data) => this.changesApplied.Add(data));
            this.resultExecutorMock.Setup(x => x.RevertChange(It.IsAny<VotingData>())).Callback((VotingData data) => this.changesReverted.Add(data));
        }

        [Fact]
        public void CanScheduleAndRemoveVotes()
        {
            this.federationManager.SetPrivatePropertyValue(typeof(FederationManagerBase), nameof(this.federationManager.IsFederationMember), true);

            this.votingManager.ScheduleVote(new VotingData());

            Assert.Single(this.votingManager.GetScheduledVotes());

            this.votingManager.ScheduleVote(new VotingData());

            Assert.Equal(2, this.votingManager.GetAndCleanScheduledVotes().Count);

            Assert.Empty(this.votingManager.GetScheduledVotes());
        }

        [Fact]
        public void CanVote()
        {
            var votingData = new VotingData()
            {
                Key = VoteKey.AddFederationMember,
                Data = RandomUtils.GetBytes(20)
            };

            int votesRequired = (this.federationManager.GetFederationMembers().Count / 2) + 1;

            for (int i = 0; i < votesRequired; i++)
            {
                TriggerOnBlockConnected(CreateBlockWithVotingData(new List<VotingData>() { votingData }, i + 1));
            }

            Assert.Single(this.votingManager.GetFinishedPolls());
        }

        [Fact]
        public void AddVoteAfterPollComplete()
        {
            //TODO: When/if we remove duplicate polls, this test will need to be changed to account for the new expected functionality.

            var votingData = new VotingData()
            {
                Key = VoteKey.AddFederationMember,
                Data = RandomUtils.GetBytes(20)
            };

            int votesRequired = (this.federationManager.GetFederationMembers().Count / 2) + 1;

            for (int i = 0; i < votesRequired; i++)
            {
                TriggerOnBlockConnected(CreateBlockWithVotingData(new List<VotingData>() { votingData }, i + 1));
            }

            Assert.Single(this.votingManager.GetFinishedPolls());
            Assert.Empty(this.votingManager.GetPendingPolls());

            // Now that poll is complete, add another vote for it.
            ChainedHeaderBlock blockToDisconnect = CreateBlockWithVotingData(new List<VotingData>() { votingData }, votesRequired + 1);
            TriggerOnBlockConnected(blockToDisconnect);

            // Now we have 1 finished and 1 pending for the same data.
            Assert.Single(this.votingManager.GetFinishedPolls());
            Assert.Single(this.votingManager.GetPendingPolls());

            // This previously caused an error because of Single() being used.
            TriggerOnBlockDisconnected(blockToDisconnect);

            // VotingManager cleverly removed the pending poll but kept the finished poll.
            Assert.Single(this.votingManager.GetFinishedPolls());
            Assert.Empty(this.votingManager.GetPendingPolls());
        }

        private ChainedHeaderBlock CreateBlockWithVotingData(List<VotingData> data, int height)
        {
            var tx = new Transaction();

            var votingData = new List<byte>(VotingDataEncoder.VotingOutputPrefixBytes);
            votingData.AddRange(this.encoder.Encode(data));

            var votingOutputScript = new Script(OpcodeType.OP_RETURN, Op.GetPushOp(votingData.ToArray()));

            tx.AddOutput(Money.COIN, votingOutputScript);

            Block block = new Block();
            block.Transactions.Add(tx);

            block.Header.Time = (uint)(height * this.network.ConsensusOptions.TargetSpacingSeconds);

            block.UpdateMerkleRoot();
            block.GetHash();

            return new ChainedHeaderBlock(block, new ChainedHeader(block.Header, block.GetHash(), height));
        }

        private void TriggerOnBlockConnected(ChainedHeaderBlock block)
        {
            this.signals.Publish(new BlockConnected(block));
        }

        private void TriggerOnBlockDisconnected(ChainedHeaderBlock block)
        {
            this.signals.Publish(new BlockDisconnected(block));
        }
    }
}
