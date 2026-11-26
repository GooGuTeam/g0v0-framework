// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

#nullable disable

using System;
using osu.Framework.Bindables;

namespace osu.Framework.Audio.Sample
{
    public abstract class Sample : AudioCollectionManager<SampleChannel>, ISample
    {
        public const int DEFAULT_CONCURRENCY = 2;

        public string Name { get; }

        protected Sample(string name)
        {
            Name = name;
        }

        public abstract double Length { get; }

        /// <summary>
        /// Encoded source for a private offline decode stream; never a live channel handle.
        /// </summary>
        public virtual byte[] GetOfflineData() => null;

        public Bindable<int> PlaybackConcurrency { get; } = new Bindable<int>(DEFAULT_CONCURRENCY);

        internal Action<Sample> RequestUpdate;

        public SampleChannel Play()
        {
            var channel = GetChannel();
            channel.Play();
            return channel;
        }

        public SampleChannel GetChannel()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            var channel = CreateChannel();
            channel.RequestUpdate = requestUpdate;
            return channel;
        }

        private void requestUpdate(SampleChannel channel)
        {
            AddItem(channel);
            RequestUpdate?.Invoke(this);
        }

        public override bool IsAlive => base.IsAlive && (!PendingActions.IsEmpty || Items.Count > 0);

        /// <summary>
        /// Creates a unique playback of this <see cref="Sample"/>.
        /// </summary>
        /// <returns>The <see cref="SampleChannel"/> for the playback.</returns>
        protected abstract SampleChannel CreateChannel();

        protected override void ItemRemoved(SampleChannel item)
        {
            base.ItemRemoved(item);

            // A seek can register a channel temporarily before its first play.
            if (!item.Played)
            {
                item.UnbindAdjustments(this);
                return;
            }

            // A finished channel is disposed unless the caller requested manual cleanup.
            if (!item.IsDisposed && !item.ManualFree)
            {
                item.Dispose();
                item.Update();
            }
        }
    }
}
