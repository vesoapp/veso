#nullable disable
#pragma warning disable CS1591

using System;
using MediaBrowser.Model.Dto;

namespace MediaBrowser.Model.Dlna
{
    /// <summary>
    /// Class AudioOptions.
    /// </summary>
    public class AudioOptions
    {
        public AudioOptions()
        {
            Context = EncodingContext.Streaming;

            EnableDirectPlay = true;
            EnableDirectStream = true;
        }

        public bool EnableDirectPlay { get; set; }

        public bool EnableDirectStream { get; set; }

        public bool ForceDirectPlay { get; set; }

        public bool ForceDirectStream { get; set; }

        public bool AllowAudioStreamCopy { get; set; }

        public Guid ItemId { get; set; }

        public MediaSourceInfo[] MediaSources { get; set; }

        public DeviceProfile Profile { get; set; }

        /// <summary>
        /// Gets or sets a media source id. Optional. Only needed if a specific AudioStreamIndex or SubtitleStreamIndex are requested.
        /// </summary>
        public string MediaSourceId { get; set; }

        public string DeviceId { get; set; }

        /// <summary>
        /// Gets or sets an override of supported number of audio channels
        /// Example: DeviceProfile supports five channel, but user only has stereo speakers.
        /// </summary>
        public int? MaxAudioChannels { get; set; }

        /// <summary>
        /// Gets or sets the application's configured quality setting.
        /// </summary>
        public int? MaxBitrate { get; set; }

        /// <summary>
        /// Gets or sets the context.
        /// </summary>
        /// <value>The context.</value>
        public EncodingContext Context { get; set; }

        /// <summary>
        /// Gets or sets the audio transcoding bitrate.
        /// </summary>
        /// <value>The audio transcoding bitrate.</value>
        public int? AudioTranscodingBitrate { get; set; }

        /// <summary>
        /// Gets the maximum bitrate.
        /// </summary>
        /// <param name="isAudio">Whether or not this is audio.</param>
        /// <returns>System.Nullable&lt;System.Int32&gt;.</returns>
        public int? GetMaxBitrate(bool isAudio)
        {
            if (MaxBitrate.HasValue)
            {
                return MaxBitrate;
            }

            if (Profile == null)
            {
                return null;
            }

            if (Context == EncodingContext.Static)
            {
                if (isAudio && Profile.MaxStaticMusicBitrate.HasValue)
                {
                    return Profile.MaxStaticMusicBitrate;
                }

                return Profile.MaxStaticBitrate;
            }

            return Profile.MaxStreamingBitrate;
        }
    }
}
