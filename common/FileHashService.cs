using System.Collections.Concurrent;

namespace common
{
	public class FileHashService
	{
		private readonly ConcurrentDictionary<string, bool> _processedFileHashes = new();

		public bool TryAddHash(string fileHash)
		{
			return _processedFileHashes.TryAdd(fileHash, true);
		}

		public bool RemoveHash(string fileHash)
		{
			return _processedFileHashes.TryRemove(fileHash, out _);
		}

		public bool ContainsHash(string fileHash)
		{
			return _processedFileHashes.ContainsKey(fileHash);
		}
	}
}
