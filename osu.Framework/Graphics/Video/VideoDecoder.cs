// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using osu.Framework.Bindables;
using osuTK;

namespace osu.Framework.Graphics.Video
{
    /// <summary>
    /// Represents a video decoder that converts video streams and files into textures.
    /// </summary>
    /// <remarks>
    /// Kept independent of FFmpeg types so platform decoders such as <see cref="MediaFoundationVideoDecoder"/> can run
    /// without loading <c>FFmpeg.AutoGen</c>.
    /// </remarks>
    public abstract class VideoDecoder : IDisposable
    {
        /// <summary>
        /// The duration of the video that is being decoded. Can only be queried after the decoder has started decoding and loaded.
        /// </summary>
        public abstract double Duration { get; }

        /// <summary>
        /// True if the decoder currently is decoding frames, false otherwise.
        /// </summary>
        public bool IsRunning => State == DecoderState.Running;

        /// <summary>
        /// True if the decoder has faulted after starting to decode. You can try to restart a failed decoder by invoking <see cref="StartDecoding"/> again.
        /// </summary>
        public bool IsFaulted => State == DecoderState.Faulted;

        /// <summary>
        /// The timestamp of the last frame that was decoded by this video decoder, or 0 if no frames have been decoded.
        /// </summary>
        public abstract float LastDecodedFrameTime { get; }

        /// <summary>
        /// The frame rate of the video stream this decoder is decoding.
        /// </summary>
        public abstract double FrameRate { get; }

        /// <summary>
        /// True if the decoder can seek, false otherwise. Determined by the stream this decoder was created with.
        /// </summary>
        public abstract bool CanSeek { get; }

        /// <summary>
        /// The current state of the decoding process.
        /// </summary>
        public abstract DecoderState State { get; }

        /// <summary>
        /// Determines which hardware acceleration device(s) should be used.
        /// </summary>
        public readonly Bindable<HardwareVideoDecoder> TargetHardwareVideoDecoders = new Bindable<HardwareVideoDecoder>();

        internal bool Looping;

        /// <summary>
        /// Seek the decoder to the given timestamp. This will fail if <see cref="CanSeek"/> is false.
        /// </summary>
        /// <param name="targetTimestamp">The timestamp to seek to.</param>
        public abstract void Seek(double targetTimestamp);

        /// <summary>
        /// Returns the given frames back to the decoder, allowing the decoder to reuse the textures contained in the frames to draw new frames.
        /// </summary>
        /// <param name="frames">The frames that should be returned to the decoder.</param>
        public abstract void ReturnFrames(IEnumerable<DecodedFrame> frames);

        /// <summary>
        /// Starts the decoding process. The decoding will happen asynchronously in a separate thread. The decoded frames can be retrieved by using <see cref="GetDecodedFrames"/>.
        /// </summary>
        public abstract void StartDecoding();

        /// <summary>
        /// Stops the decoding process.
        /// </summary>
        public abstract Task StopDecodingAsync();

        /// <summary>
        /// Gets all frames that have been decoded by the decoder up until the point in time when this method was called.
        /// Retrieving decoded frames using this method consumes them, ie calling this method again will never retrieve the same frame twice.
        /// </summary>
        /// <returns>The frames that have been decoded up until the point in time this method was called.</returns>
        public abstract IEnumerable<DecodedFrame> GetDecodedFrames();

        // https://en.wikipedia.org/wiki/YCbCr
        public abstract Matrix3 GetConversionMatrix();

        #region Disposal

        ~VideoDecoder()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected abstract void Dispose(bool disposing);

        #endregion

        /// <summary>
        /// Represents the possible states the decoder can be in.
        /// </summary>
        public enum DecoderState
        {
            /// <summary>
            /// The decoder is ready to begin decoding. This is the default state before the decoder starts operations.
            /// </summary>
            Ready = 0,

            /// <summary>
            /// The decoder is currently running and decoding frames.
            /// </summary>
            Running = 1,

            /// <summary>
            /// The decoder has faulted with an exception.
            /// </summary>
            Faulted = 2,

            /// <summary>
            /// The decoder has reached the end of the video data.
            /// </summary>
            EndOfStream = 3,

            /// <summary>
            /// The decoder has been completely stopped and cannot be resumed.
            /// </summary>
            Stopped = 4,
        }
    }
}
