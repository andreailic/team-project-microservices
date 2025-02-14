﻿using System;
using Blockcore.EventBus;
using Blockcore.EventBus.CoreEvents;
using Blockcore.Features.Miner;
using Blockcore.Signals;
using Blockcore.Utilities;
using Blockcore.Utilities.Extensions;

namespace Blockcore.IntegrationTests.Common.EnvironmentMockUpHelpers
{
    /// <summary>
    /// This date time provider substitutes the node's usual DTP when running certain
    /// integration tests so that we can generate coins faster.
    /// </summary>
    public sealed class GenerateCoinsFastDateTimeProvider : IDateTimeProvider
    {
        private static TimeSpan adjustedTimeOffset;
        private static DateTime startFrom;

        private readonly SubscriptionToken blockConnectedSubscription;

        static GenerateCoinsFastDateTimeProvider()
        {
            adjustedTimeOffset = TimeSpan.Zero;
            startFrom = new DateTime(2018, 1, 1);
        }

        public GenerateCoinsFastDateTimeProvider(ISignals signals)
        {
            this.blockConnectedSubscription = signals.Subscribe<BlockConnected>(OnBlockConnected);
        }

        public long GetTime()
        {
            return startFrom.ToUnixTimestamp();
        }

        public DateTime GetUtcNow()
        {
            return startFrom;
        }

        /// <summary>
        /// This gets called when the Transaction's time gets set in <see cref="PowBlockDefinition"/>.
        /// </summary>
        public DateTime GetAdjustedTime()
        {
            return startFrom;
        }

        /// <summary>
        /// This gets called when the Block Header's time gets set in <see cref="PowBlockDefinition"/>.
        /// <para>
        /// Please see the <see cref="PowBlockDefinition.UpdateHeaders"/> method.
        /// </para>
        /// <para>
        /// Add 5 seconds to the time so that the block header's time stamp is after
        /// the transaction's creation time.
        /// </para>
        /// </summary>
        public DateTimeOffset GetTimeOffset()
        {
            return startFrom;
        }

        /// <summary>
        /// This gets called when the coin stake block gets created in <see cref="PosMinting"/>.
        /// This gets called when the transaction's time gets set in <see cref="Features.Miner.PowBlockDefinition"/>.
        /// <para>
        /// Please see the <see cref="PosMinting.GenerateBlocksAsync"/> method.
        /// </para>
        /// <para>
        /// Please see the <see cref="Features.Miner.PowBlockDefinition.CreateCoinbase"/> method.
        /// </para>
        /// </summary>
        public long GetAdjustedTimeAsUnixTimestamp()
        {
            return startFrom.ToUnixTimestamp();
        }

        public void SetAdjustedTimeOffset(TimeSpan adjusted)
        {
            adjustedTimeOffset = adjusted;
        }

        /// <summary>
        /// Every time a new block gets generated, this date time provider will be signaled,
        /// updating the last block time by 65 seconds.
        /// </summary>
        private void OnBlockConnected(BlockConnected blockConnected)
        {
            startFrom = startFrom.AddSeconds(65);
        }
    }
}