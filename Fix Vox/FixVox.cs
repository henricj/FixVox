// Copyright (c) 2015 Henric Jungheim <software@henric.org>
// 
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
// THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TagLib;
using File = TagLib.File;
using Tag = TagLib.Id3v2.Tag;

namespace FixVox
{
    class FixVox
    {
        public static readonly ReadOnlyByteVector TPUB = "TPUB";
        public static readonly ReadOnlyByteVector TLEN = "TLEN";
        public static readonly ReadOnlyByteVector TRCK = "TRCK";

        static readonly char[] InvalidFileNameChars;
        static readonly HashSet<char> InvalidFileNameCharSet;
        static readonly TaskScheduler ZipScheduler = new LimitedConcurrencyLevelTaskScheduler(Math.Max(1, Environment.ProcessorCount / 2));
        Dictionary<string, string> _artists;

        Configuration _configuration;
        DirectoryInfo _outputDirectory;

        static FixVox()
        {
            InvalidFileNameChars = Path.GetInvalidFileNameChars();
            InvalidFileNameCharSet = new HashSet<char>(InvalidFileNameChars);
        }

        public async Task<bool> TransformFileAsync(Stream inputStream, DirectoryInfo directoryInfo)
        {
            using (var zip = new ZipArchive(inputStream, ZipArchiveMode.Read))
            {
                var sorted = zip.Entries
                                .Where(e => string.Equals(Path.GetExtension(e.Name), ".mp3", StringComparison.OrdinalIgnoreCase))
                                .OrderBy(e => e.Name, StringComparer.InvariantCultureIgnoreCase)
                                .ToArray();

                var dir = directoryInfo.FullName;

                // ReSharper disable once AccessToDisposedClosure
                await Task.Factory.StartNew(() => zip.ExtractToDirectory(dir), CancellationToken.None, TaskCreationOptions.LongRunning, ZipScheduler).ConfigureAwait(false);

                var files = directoryInfo.EnumerateFiles("*.mp3")
                                         .OrderBy(fi => fi.Name, StringComparer.InvariantCultureIgnoreCase)
                                         .ToArray();

                // TODO: Compare "files" with "sorted"?  They should match.

                Debug.Assert(sorted.Length == files.Length, "Sorted/files mismatch");

                var length = files.Length;

                var trackFormat = GetTrackFormat((uint)length);

                var tasks = new List<Task>(length);

                for (var i = 0; i < sorted.Length; ++i)
                {
                    var trackId = i + 1;
                    var fileInfo = files[i];

                    var task = FixEntryAsync(fileInfo, (uint)trackId, (uint)length, trackFormat);

                    tasks.Add(task);
                }

                await Task.WhenAll(tasks);
            }

            return true;
        }

        static string GetTrackFormat(uint tracks)
        {
            var digits = 1;

            while (tracks >= 10)
            {
                tracks /= 10;
                ++digits;
            }

            var sb = new StringBuilder(digits);

            sb.Append('0', digits);

            return sb.ToString();
        }

        static string GetTrackName(string trackFormat, uint trackId)
        {
            return trackId.ToString(trackFormat, CultureInfo.InvariantCulture) + ".mp3";
        }

        async Task FixEntryAsync(FileInfo fileInfo, uint trackId, uint trackCount, string trackFormat)
        {
            using (var readStream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous))
            {
                string album = null;
                string artist = null;

                using (var readFile = File.Create(new NonClosingFileAbstraction(fileInfo.FullName, readStream)))
                {
                    var tag = readFile.GetTag(TagTypes.Id3v2);

                    if (null != tag)
                    {
                        album = tag.Album;
                        artist = tag.FirstAlbumArtist ?? tag.FirstPerformer;
                    }
                }

                if (string.IsNullOrWhiteSpace(album))
                    album = Path.GetFileNameWithoutExtension(fileInfo.Name);
                else
                    album = ScrubFileName(album);

                if (string.IsNullOrWhiteSpace(artist))
                    artist = "Unknown";
                else
                {
                    string preferredArtist;
                    if (_artists.TryGetValue(artist, out preferredArtist))
                        artist = preferredArtist;

                    artist = ScrubFileName(artist);
                }

                readStream.Seek(0, SeekOrigin.Begin);

                var trackName = GetTrackName(trackFormat, trackId);

                var trackPath = Path.Combine(_outputDirectory.FullName, artist, album);

                var trackDir = new DirectoryInfo(trackPath);

                if (!trackDir.Exists)
                    trackDir.Create();

                var fullTrackName = Path.Combine(trackPath, trackName);

                using (var writeStream = new FileStream(fullTrackName, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                {
                    await readStream.CopyToAsync(writeStream, 64 * 1024).ConfigureAwait(false);

                    readStream.Seek(0, SeekOrigin.Begin);

                    var duration = await MeasureDurationAsync(readStream).ConfigureAwait(false);

                    writeStream.Seek(0, SeekOrigin.Begin);

                    //await writeStream.FlushAsync().ConfigureAwait(false);

                    using (var writeFile = File.Create(new NonClosingFileAbstraction(trackName, writeStream)))
                    {
                        var writeTags = writeFile.GetTag(TagTypes.Id3v2, true) as Tag;

                        if (null == writeTags)
                            return;

                        writeTags.Disc = 1;
                        writeTags.DiscCount = 1;

                        writeTags.TrackCount = trackCount;
                        writeTags.SetTextFrame(TRCK, trackId.ToString(trackFormat, CultureInfo.InvariantCulture) + '/' + trackCount.ToString(trackFormat, CultureInfo.InvariantCulture));

                        writeTags.SetTextFrame(TPUB, "LibriVox");

                        writeTags.SetTextFrame(TLEN, duration.TotalMilliseconds.ToString(NumberFormatInfo.InvariantInfo));

                        writeTags.Genres = new[] { "Audiobook" };

                        writeFile.Save();
                    }
                }
            }
        }

        static string ScrubFileName(string path)
        {
            path = path.Normalize();

            var idx = path.IndexOfAny(InvalidFileNameChars);

            if (idx >= 0)
            {
                var sb = new StringBuilder();

                foreach (var c in path)
                {
                    if (!InvalidFileNameCharSet.Contains(c))
                        sb.Append(c);
                }

                path = sb.ToString();
            }

            return path.Trim();
        }

        static int? GetId3Length(byte[] buffer, int offset, int length)
        {
            if (length < 10)
                return null;

            if ('I' != buffer[offset] || 'D' != buffer[offset + 1] || '3' != buffer[offset + 2])
                return null;

            var majorVersion = buffer[offset + 3];

            if (0xff == majorVersion)
                return null;

            var minorVersion = buffer[offset + 4];

            if (0xff == minorVersion)
                return null;

            var flags = buffer[offset + 5];

            var size = 0;

            for (var i = 0; i < 4; ++i)
            {
                var b = buffer[offset + 6 + i];

                if (0 != (0x80 & b))
                    return null;

                size <<= 7;

                size |= b;
            }

            return size;
        }

        static async Task<TimeSpan> MeasureDurationAsync(Stream input)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

            var mp3 = new Mp3FrameHeader();
            var total = TimeSpan.Zero;
            var bufferOffset = 0;
            var skip = 0;
            var isFirst = true;

            for (; ; )
            {
                var read = await input.ReadAsync(buffer.AsMemory(bufferOffset, buffer.Length - bufferOffset)).ConfigureAwait(false);

                if (read < 1)
                    break;

                read += bufferOffset;

                var position = 0;

                if (isFirst)
                {
                    isFirst = false;

                    var id3Length = GetId3Length(buffer, 0, read);

                    if (id3Length.HasValue)
                        skip = id3Length.Value;
                }

                if (skip > 0)
                {
                    if (read <= skip)
                    {
                        skip -= read;

                        continue;
                    }

                    position += skip;
                    read -= skip;

                    skip = 0;
                }

                while (position + 4 < read)
                {
                    if (!mp3.Parse(buffer, position, read - position))
                    {
                        position += Math.Max(mp3.HeaderOffset, 1);
                        continue;
                    }

                    position += mp3.HeaderOffset;

                    if (position + mp3.FrameLength <= read)
                    {
                        total += mp3.Duration;
                        position += mp3.FrameLength;
                    }

                    if (position + mp3.FrameLength >= read)
                        break;
                }

                if (position < read)
                {
                    bufferOffset = read - position;
                    Array.Copy(buffer, position, buffer, 0, bufferOffset);
                }
                else if (position == read)
                    bufferOffset = 0;
                else
                {
                    Debug.Assert(false, "Can't happen...");
                }
            }

            ArrayPool<byte>.Shared.Return(buffer);

            return total;
        }

        async Task LoadAsync()
        {
            var configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FixVox");

            var path = Path.Combine(configPath, "configuration.json");

            _configuration = await Utils.DeserializeAsync<Configuration>(path).ConfigureAwait(false);

            path = Path.Combine(configPath, "artists.json");

            _artists = await Utils.DeserializeAsync<Dictionary<string, string>>(path).ConfigureAwait(false);
        }

        public async Task ConfigureAsync()
        {
            await LoadAsync().ConfigureAwait(false);

            var dir = new DirectoryInfo(_configuration.OutputFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "FixVox"));

            if (!dir.Exists)
                dir.Create();

            _outputDirectory = dir;
        }

        #region Nested type: Configuration

        class Configuration
        {
            public string OutputFolder { get; set; }
        }

        #endregion

        #region Nested type: NonClosingFileAbstraction

        class NonClosingFileAbstraction : File.IFileAbstraction
        {
            public NonClosingFileAbstraction(string name, Stream stream)
            {
                Name = name;
                ReadStream = stream;
                WriteStream = stream;
            }

            #region IFileAbstraction Members

            public string Name { get; private set; }
            public Stream ReadStream { get; private set; }
            public Stream WriteStream { get; private set; }

            public void CloseStream(Stream stream)
            {
                // Nope...
            }

            #endregion
        }

        #endregion
    }
}
