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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FixVox
{
    public sealed class FileProcessor : IDisposable
    {
        readonly TempDirManager _tempDirManager = new TempDirManager();

        #region IDisposable Members

        public void Dispose()
        {
            _tempDirManager.Dispose();
        }

        #endregion

        public async Task<IEnumerable<string>> ProcessFilesAsync(string[] args, Func<Stream, DirectoryInfo, Task<bool>> transform)
        {
            var fileTasks = args.AsParallel()
                                .SelectMany(arg =>
                                            {
                                                try
                                                {
                                                    var attr = File.GetAttributes(arg);

                                                    if (FileAttributes.Directory == (attr & FileAttributes.Directory))
                                                        return Directory.EnumerateFiles(arg, "*.zip", SearchOption.AllDirectories);

                                                    if (0 == (attr & (FileAttributes.ReadOnly | FileAttributes.Offline | FileAttributes.ReparsePoint)))
                                                    {
                                                        var fileInfo = new FileInfo(arg);

                                                        return new[] { fileInfo.FullName };
                                                    }
                                                }
                                                catch (IOException)
                                                { }

                                                return new string[] { };
                                            })
                                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                                .Select(filename => ProcessFileAsync(filename, transform))
                                .ToArray();

            await Task.WhenAll(fileTasks).ConfigureAwait(false);

            return fileTasks.Select(fileTask => fileTask.Result)
                            .Where(fileName => null != fileName)
                            .ToArray();
        }

        async Task<string> ProcessFileAsync(string filename, Func<Stream, DirectoryInfo, Task<bool>> transform)
        {
            var fileInfo = new FileInfo(filename);

            if (fileInfo.Length < 1)
                return fileInfo.FullName;

            Exception exception = null;

            try
            {
                var tempDirTask = _tempDirManager.GetDirectoryAsync(fileInfo.Name);

                using (var inputStream = await CreateReadStreamAsync(fileInfo).ConfigureAwait(false))
                {
                    var tempDir = await tempDirTask.ConfigureAwait(false);

                    if (!await transform(inputStream, tempDir).ConfigureAwait(false))
                        return null;
                }
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            try
            {
                await _tempDirManager.CleanupDirectoryAsync(fileInfo.Name).ConfigureAwait(false);
            }
            catch (Exception)
            {
                Debug.WriteLine("Temp dir cleanup failed");
            }

            if (null != exception)
                throw exception;

            return fileInfo.FullName;
        }

        static void ReplaceFile(FileInfo fileInfo, string newFile, DirectoryInfo tempDir)
        {
            File.SetCreationTimeUtc(newFile, fileInfo.CreationTimeUtc);
            File.SetLastWriteTimeUtc(newFile, fileInfo.LastWriteTimeUtc);
            File.SetLastAccessTimeUtc(newFile, fileInfo.LastAccessTimeUtc);

            var fullName = fileInfo.FullName;

            var backupFileName = CreateTempFilename(tempDir, fileInfo);

            File.Move(fullName, backupFileName);

            try
            {
                File.Move(newFile, fullName);
            }
            catch (IOException)
            {
                // Restore the original file to it's original name.
                File.Move(backupFileName, fullName);

                throw;
            }
            try
            {
                File.Delete(backupFileName);
            }
            catch (IOException)
            {
                Debug.WriteLine("Unable to delete backup file");
            }
        }

        static async Task<Stream> CreateReadStreamAsync(FileInfo fileInfo)
        {
            var fileStream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (fileInfo.Length > 512 * 1024)
                return fileStream;

            using (fileStream)
            {
                var ms = new MemoryStream((int)fileInfo.Length);

                await fileStream.CopyToAsync(ms).ConfigureAwait(false);

                ms.Seek(0, SeekOrigin.Begin);

                return ms;
            }
        }

        static string CreateTempFilename(DirectoryInfo tempDir, FileInfo fileInfo)
        {
            var baseTempFilename = Path.Combine(tempDir.FullName, Path.GetFileNameWithoutExtension(fileInfo.Name));

            for (var retry = 0; retry < 4; ++retry)
            {
                var tempFilename = Path.ChangeExtension(baseTempFilename, Guid.NewGuid().ToString("N"));

                if (!File.Exists(tempFilename))
                    return tempFilename;
            }

            throw new IOException("Unable to create temporary filename");
        }
    }
}
