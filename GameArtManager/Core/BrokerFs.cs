using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Search;
using Windows.Storage.Streams;

namespace GameArtManager.Core
{
    /// <summary>
    /// Windows.Storage (broker) I/O helpers. On some machines direct System.IO access
    /// to another package's folder is denied even though the broker path works, so
    /// these wrap the WinRT APIs as a fallback. Every method swallows exceptions and
    /// returns null/false — they never throw.
    /// </summary>
    public static class BrokerFs
    {
        /// <summary>Opens a folder via StorageFolder.GetFolderFromPathAsync; null on failure.</summary>
        public static async Task<StorageFolder> TryGetFolderAsync(string path)
        {
            try
            {
                return await StorageFolder.GetFolderFromPathAsync(path);
            }
            catch
            {
                return null;
            }
        }

        public static async Task<bool> FolderExistsAsync(string path)
        {
            return await TryGetFolderAsync(path) != null;
        }

        /// <summary>Subfolder NAMES of a directory (empty list on failure).</summary>
        public static async Task<List<string>> GetSubDirectoriesAsync(string path)
        {
            var names = new List<string>();
            try
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(path);
                foreach (var sub in await folder.GetFoldersAsync())
                    names.Add(sub.Name);
            }
            catch
            {
                // inaccessible -> empty list
            }
            return names;
        }

        /// <summary>FULL paths of the files in a directory (empty list on failure).</summary>
        public static async Task<List<string>> GetFilesAsync(string path)
        {
            var files = new List<string>();
            try
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(path);
                foreach (var file in await folder.GetFilesAsync(CommonFileQuery.DefaultQuery))
                    files.Add(file.Path);
            }
            catch
            {
                // inaccessible -> empty list
            }
            return files;
        }

        /// <summary>Reads a file via StorageFile + FileIO; null on failure.</summary>
        public static async Task<string> ReadAllTextAsync(string path)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                return await FileIO.ReadTextAsync(file);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Writes bytes, creating/replacing the file in its parent folder.</summary>
        public static async Task<bool> WriteAllBytesAsync(string path, byte[] data)
        {
            try
            {
                var dir = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path));
                var file = await dir.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, data);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>True when the broker can open the file (catch -> false).</summary>
        public static async Task<bool> FileExistsAsync(string path)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                return file != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Deletes a file; true when deleted, false when missing/inaccessible.</summary>
        public static async Task<bool> DeleteFileAsync(string path)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                await file.DeleteAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Moves a file, replacing the destination (deleted first, best effort).</summary>
        public static async Task<bool> MoveFileAsync(string from, string to)
        {
            try
            {
                if (await FileExistsAsync(to))
                    await DeleteFileAsync(to); // best effort
                var file = await StorageFile.GetFileFromPathAsync(from);
                var dir = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(to));
                await file.MoveAsync(dir, Path.GetFileName(to), NameCollisionOption.ReplaceExisting);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Opens a file for reading (thumbnails); null on failure.</summary>
        public static async Task<IRandomAccessStream> OpenReadAsync(string path)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                return await file.OpenReadAsync();
            }
            catch
            {
                return null;
            }
        }
    }
}
