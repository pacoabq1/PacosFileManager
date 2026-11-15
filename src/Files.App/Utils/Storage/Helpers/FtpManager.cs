// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Net;
using FluentFTP;

namespace Files.App.Utils.Storage
{
	public static class FtpManager
	{
		public static readonly Dictionary<string, NetworkCredential> Credentials = [];

		internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, NetworkCredential> SiteCredentials = new(StringComparer.OrdinalIgnoreCase);

		internal static string SiteKey(string host, int port, FtpEncryptionMode mode) => $"{mode}/{host}/{port}";

		private static readonly SemaphoreSlim ListingGate = new(1);
		private static AsyncFtpClient? _listingClient;
		private static string? _listingKey;

		internal static async Task<FtpListItem[]> ListFolderAsync(string path, NetworkCredential fallback, CancellationToken token)
		{
			await ListingGate.WaitAsync(token);
			try
			{
				var host = FtpHelpers.GetFtpHost(path);
				var port = FtpHelpers.GetFtpPort(path);
				var mode = GetMode(path);
				var key = SiteKey(host, port, mode);
				var credential = SiteCredentials.GetValueOrDefault(key, fallback);
				if (_listingClient is not { IsConnected: true } existing ||
					_listingKey != key || existing.Credentials.UserName != credential.UserName ||
					existing.Credentials.Password != credential.Password)
				{
					_listingClient?.Dispose();
					_listingClient = CreateClient(path, credential);
					_listingKey = key;
					await _listingClient.Connect(token);
				}

				var directory = FtpHelpers.GetFtpPath(path);
				try
				{
					return await _listingClient.GetListing(directory, token);
				}
				catch (FluentFTP.Exceptions.FtpCommandException ex) when (ex.CompletionCode is "500" or "502")
				{
					return await _listingClient.GetListing(directory, FtpListOption.ForceList, token);
				}
			}
			catch
			{
				_listingClient?.Dispose();
				_listingClient = null;
				throw;
			}
			finally
			{
				ListingGate.Release();
			}
		}

		private static FtpEncryptionMode GetMode(string path) =>
			path.StartsWith("ftpes://", StringComparison.OrdinalIgnoreCase) ? FtpEncryptionMode.Explicit :
			path.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase) ? FtpEncryptionMode.Implicit : FtpEncryptionMode.None;

		internal static AsyncFtpClient CreateClient(string path, NetworkCredential fallback)
		{
			var host = FtpHelpers.GetFtpHost(path);
			var port = FtpHelpers.GetFtpPort(path);
			var mode = GetMode(path);
			var credential = SiteCredentials.GetValueOrDefault(SiteKey(host, port, mode), fallback);
			var client = new AsyncFtpClient(host, credential, port);
			client.Config.EncryptionMode = mode;
			client.Config.DataConnectionEncryption = mode != FtpEncryptionMode.None;
			client.Config.ValidateAnyCertificate = false;
			if (Uri.CheckHostName(host) != UriHostNameType.IPv6)
				client.Config.DataConnectionType = FtpDataConnectionType.PASVEX;
			return client;
		}

		public static readonly NetworkCredential Anonymous = new("anonymous", "anonymous");
	}
}
