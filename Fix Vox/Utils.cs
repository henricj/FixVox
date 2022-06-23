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

using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace FixVox
{
    static class Utils
    {
        public static async Task<Stream> CopyToMemoryAsync(FileInfo fileInfo)
        {
            var ms = new MemoryStream(checked((int)fileInfo.Length));

            await using (var file = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await file.CopyToAsync(ms).ConfigureAwait(false);
            }

            ms.Seek(0, SeekOrigin.Begin);

            return ms;
        }

        public static async Task<TextReader> CopyToMemoryReaderAsync(FileInfo fileInfo)
        {
            return new StreamReader(await CopyToMemoryAsync(fileInfo).ConfigureAwait(false));
        }

        public static async Task<string> ReadStringAsync(FileInfo fileInfo)
        {
            using var sr = await CopyToMemoryReaderAsync(fileInfo).ConfigureAwait(false);

            return await sr.ReadToEndAsync().ConfigureAwait(false);
        }

        public static async ValueTask<TResult> DeserializeAsync<TResult>(FileInfo fileInfo)
        where TResult : new()
        {
            await using var stream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.Asynchronous | FileOptions.SequentialScan);

            return await JsonSerializer.DeserializeAsync<TResult>(stream).ConfigureAwait(false);
        }

        public static async ValueTask<TResult> DeserializeAsync<TResult>(string path)
            where TResult : new()
        {
            var fileInfo = new FileInfo(path);

            if (!fileInfo.Exists)
                return new TResult();

            return await DeserializeAsync<TResult>(fileInfo).ConfigureAwait(false);
        }
    }
}
