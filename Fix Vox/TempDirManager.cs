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
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FixVox
{
    public sealed class TempDirManager
    {
        readonly ConcurrentDictionary<string, Task<DirectoryInfo>> _tempDirs = new ConcurrentDictionary<string, Task<DirectoryInfo>>(StringComparer.InvariantCultureIgnoreCase);
        readonly DirectoryInfo _temporaryDirectory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "FixVox-" + Path.GetRandomFileName()));

        public void Dispose()
        {
            var dirs = _tempDirs.Select(kv => kv.Value).Cast<Task>().ToArray();

            _tempDirs.Clear();

            try
            {
                Task.WaitAll(dirs);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Directory cleanup failed: " + ex.Message);
            }

            for (var retry = 0; retry < 3; ++retry)
            {
                try
                {
                    _temporaryDirectory.Refresh();

                    if (_temporaryDirectory.Exists)
                        _temporaryDirectory.Delete();

                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Directory cleanup failed: " + ex.Message);
                }

                Delays.ShortDelay().Wait();
            }
        }

        public async Task CleanupDirectoryAsync(string key)
        {
            Task<DirectoryInfo> dirTask;
            if (!_tempDirs.TryRemove(key, out dirTask))
                return;

            try
            {
                var dir = await dirTask.ConfigureAwait(false);

                dir.Refresh();

                if (!dir.Exists)
                    return;

                for (; ; )
                {
                    try
                    {
                        await Task.Run(() => dir.Delete(true)).ConfigureAwait(false);

                        dir.Refresh();

                        if (!dir.Exists)
                            return;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Directory delete or refresh failed: " + ex.Message);
                    }

                    await Delays.ShortDelay().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Directory cleanup failed: " + ex.Message);
            }
        }

        public Task<DirectoryInfo> GetDirectoryAsync(string key)
        {
            for (; ; )
            {
                Task<DirectoryInfo> directoryInfoTask;
                if (_tempDirs.TryGetValue(key, out directoryInfoTask))
                    return directoryInfoTask;

                var task = new Task<Task<DirectoryInfo>>(
                    async () =>
                    {
                        for (var retry = 0; retry < 5; ++retry)
                        {
                            var path = Path.Combine(_temporaryDirectory.FullName, key + '-' + Path.GetRandomFileName());

                            var directoryInfo = new DirectoryInfo(path);

                            if (directoryInfo.Exists)
                                continue;

                            try
                            {
                                directoryInfo.Create();

                                return directoryInfo;
                            }
                            catch (IOException)
                            {
                                if (retry > 2)
                                    throw;
                            }

                            await Delays.ShortDelay().ConfigureAwait(false);
                        }

                        throw new IOException("Unable to create directory");
                    });

                var dirTask = task.Unwrap();
                if (_tempDirs.TryAdd(key, dirTask))
                {
                    task.Start();

                    return dirTask;
                }
            }
        }
    }
}
