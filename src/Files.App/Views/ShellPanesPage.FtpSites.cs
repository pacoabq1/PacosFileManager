// Copyright (c) Files Community
// Licensed under the MIT License.

using FluentFTP;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using System.Net;
using System.IO;
using FileAttributes = System.IO.FileAttributes;

namespace Files.App.Views
{
	public sealed partial class ShellPanesPage
	{
		private const string FtpSitesKey = "Paco.FtpSites";
		private readonly ObservableCollection<SavedFtpSite> _ftpSites = [];

		[WinRT.GeneratedBindableCustomProperty([nameof(SavedFtpSite.Name)], [])]
		private sealed partial record SavedFtpSite(Guid Id, string Name, string Host, int Port, int Protocol, string User, string Folder);

		[JsonSerializable(typeof(SavedFtpSite[]))]
		private sealed partial class FtpSiteJsonContext : JsonSerializerContext;

		private void LoadFtpSites()
		{
			SavedFtpSites.ItemsSource = _ftpSites;
			if (ApplicationData.Current.LocalSettings.Values[FtpSitesKey] is not string json)
				return;

			try
			{
				foreach (var site in JsonSerializer.Deserialize(json, FtpSiteJsonContext.Default.SavedFtpSiteArray) ?? [])
					_ftpSites.Add(site);
			}
			catch (JsonException)
			{
				RemoteStatus.Text = "Saved FTP sites could not be read.";
			}
		}

		private void StoreFtpSites()
		{
			ApplicationData.Current.LocalSettings.Values[FtpSitesKey] =
				JsonSerializer.Serialize(_ftpSites.ToArray(), FtpSiteJsonContext.Default.SavedFtpSiteArray);
		}

		private void SaveFtpSite_Click(object sender, RoutedEventArgs e)
		{
			var name = FtpSiteName.Text.Trim();
			var host = RemoteHost.Text.Trim();
			var user = RemoteUser.Text.Trim();
			var folder = FtpStartPath.Text.Trim();
			var protocol = RemoteProtocol.SelectedIndex;
			int port = protocol == 2 ? 990 : 21;
			if (name.Length == 0 || user.Length == 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown ||
				protocol is < 1 or > 3 || !ValidFtpFolder(folder) ||
				(!string.IsNullOrWhiteSpace(RemotePort.Text) &&
				(!int.TryParse(RemotePort.Text, out port) || port is < 1 or > 65535)))
			{
				RemoteStatus.Text = "Enter a site name, host, username, and a saved folder starting with /.";
				return;
			}

			try
			{
				var existing = SavedFtpSites.SelectedItem as SavedFtpSite ??
					_ftpSites.FirstOrDefault(site => string.Equals(site.Name, name, StringComparison.OrdinalIgnoreCase));
				var site = new SavedFtpSite(existing?.Id ?? Guid.NewGuid(), name, host, port, protocol, user, folder);
				var password = RemotePassword.Password;
				if (password.Length == 0 && existing?.User == user && existing.Host == host &&
					existing.Port == port && existing.Protocol == protocol)
					password = CredentialsHelpers.GetPassword(FtpVaultKey(site), user);
				if (password.Length == 0)
				{
					RemoteStatus.Text = "Enter the FTP password before saving this site.";
					return;
				}

				CredentialsHelpers.SavePassword(FtpVaultKey(site), user, password);
				if (existing is null)
					_ftpSites.Add(site);
				else
					_ftpSites[_ftpSites.IndexOf(existing)] = site;
				StoreFtpSites();
				SavedFtpSites.SelectedItem = null;
				RemoteStatus.Text = $"Saved {site.Name}.";
			}
			catch (Exception ex)
			{
				RemoteStatus.Text = $"Could not save FTP site. {ex.Message}";
			}
		}

		private async void QuickFtpConnect_Click(object sender, RoutedEventArgs e)
		{
			if (sender is not Button { Tag: SavedFtpSite site } || _remoteOperation is not null)
				return;

			if (_remoteClient is not null)
				DisconnectRemote();
			SavedFtpSites.SelectedItem = site;
			FtpSiteName.Text = site.Name;
			RemoteHost.Text = site.Host;
			RemotePort.Text = site.Port.ToString();
			RemoteProtocol.SelectedIndex = site.Protocol;
			RemoteUser.Text = site.User;
			FtpStartPath.Text = site.Folder;
			RemotePassword.Password = CredentialsHelpers.GetPassword(FtpVaultKey(site), site.User);
			if (RemotePassword.Password.Length == 0)
			{
				RemoteStatus.Text = "Enter the password and save the site again.";
				return;
			}

			await ConnectSavedFtpSiteAsync(site, ActivePaneOrColumn);
		}

		private Task ConnectSavedFtpSiteAsync(SavedFtpSite site, IShellPage? pane)
		{
			var mode = site.Protocol switch
			{
				1 => FtpEncryptionMode.Explicit,
				2 => FtpEncryptionMode.Implicit,
				_ => FtpEncryptionMode.None,
			};
			return RunRemoteOperationAsync(async token =>
			{
				if (mode == FtpEncryptionMode.None &&
					!await ConfirmRemoteAsync("Use plain FTP?", "The server will receive your password and files without encryption.", "Connect"))
					return;

				var url = FtpUrl(site.Host, site.Port, mode, site.Folder);
				var credential = new NetworkCredential(site.User, RemotePassword.Password);
				var key = FtpManager.SiteKey(site.Host, site.Port, mode);
				var client = FtpManager.CreateClient(url, credential);
				client.Credentials = credential;
				try
				{
					await client.Connect(token);
					await ListRemoteAsync(client, site.Folder, token);
					FtpManager.SiteCredentials[key] = credential;
					_remoteClient = client;
					RemotePassword.Password = string.Empty;
					RemoteConnect.Content = "Disconnect";
					RemoteBrowser.Visibility = Visibility.Visible;
					pane?.NavigateToPath(url);
				}
				finally
				{
					if (!ReferenceEquals(_remoteClient, client))
						client.Dispose();
				}
			});
		}

		private void SaveFtpPath_Click(object sender, RoutedEventArgs e)
		{
			var path = ActivePaneOrColumn?.ShellViewModel?.WorkingDirectory;
			if (!FtpHelpers.IsFtpPath(path) || SavedFtpSites.SelectedItem is not SavedFtpSite site ||
				!string.Equals(FtpHelpers.GetFtpHost(path), site.Host, StringComparison.OrdinalIgnoreCase) ||
				FtpHelpers.GetFtpPort(path) != site.Port ||
				site.Protocol != (path.StartsWith("ftpes://", StringComparison.OrdinalIgnoreCase) ? 1 :
					path.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase) ? 2 : 3))
			{
				RemoteStatus.Text = "Quick connect a saved site and focus its FTP pane before saving the current folder.";
				return;
			}

			FtpStartPath.Text = FtpHelpers.GetFtpPath(path);
			var updated = site with { Folder = FtpStartPath.Text };
			_ftpSites[_ftpSites.IndexOf(site)] = updated;
			SavedFtpSites.SelectedItem = updated;
			StoreFtpSites();
			RemoteStatus.Text = $"Saved {updated.Folder} for {updated.Name}.";
		}

		private static string FtpVaultKey(SavedFtpSite site) => $"Paco.FtpSite/{site.Id}";
		private static bool ValidFtpFolder(string path) => path.StartsWith('/') && !path.Contains('\r') && !path.Contains('\n');

		private static string FtpUrl(string host, int port, FtpEncryptionMode mode, string path)
		{
			var scheme = mode == FtpEncryptionMode.Explicit ? "ftpes" : mode == FtpEncryptionMode.Implicit ? "ftps" : "ftp";
			return new UriBuilder(scheme, host, port).Uri.GetLeftPart(UriPartial.Authority) + path;
		}

		private async void SyncFtpPanes_Click(object sender, RoutedEventArgs e)
		{
			var paths = GetPanes().Select(pane => pane.ShellViewModel?.WorkingDirectory).ToArray();
			var ftp = paths.FirstOrDefault(path => FtpHelpers.IsFtpPath(path));
			var local = paths.FirstOrDefault(path => !FtpHelpers.IsFtpPath(path) && Directory.Exists(path));
			if (ftp is null || local is null)
			{
				RemoteStatus.Text = "Open a local folder and an FTP folder in the two main panes.";
				return;
			}

			await RunRemoteOperationAsync(async token =>
			{
				var remote = FtpHelpers.GetFtpPath(ftp);
				if (!await ConfirmRemoteAsync("Sync folders both ways?", $"{local}\n{ftp}\nMissing files will be copied in both directions. Existing files will be kept.", "Sync"))
					return;

				using var client = FtpManager.CreateClient(ftp, FtpManager.Anonymous);
				await client.Connect(token);
				int copied = 0;
				int conflicts = 0;
				await SyncFolderAsync(local, remote);
				RemoteStatus.Text = $"Sync finished. {copied} files copied, {conflicts} conflicts skipped.";

				async Task SyncFolderAsync(string localFolder, string remoteFolder)
				{
					token.ThrowIfCancellationRequested();
					var remoteItems = (await client.GetListing(remoteFolder, token))
						.ToLookup(item => item.Name, StringComparer.OrdinalIgnoreCase);
					var localItems = Directory.EnumerateFileSystemEntries(localFolder)
						.ToDictionary(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
					foreach (var group in remoteItems)
					{
						token.ThrowIfCancellationRequested();
						var item = group.First();
						if (group.Count() != 1 || !CanSyncName(item.Name) || item.Type == FtpObjectType.Link)
						{
							conflicts++;
							continue;
						}
						var target = Path.Combine(localFolder, item.Name);
						if (localItems.TryGetValue(item.Name, out var existing))
						{
							var attributes = File.GetAttributes(existing);
							if ((attributes & FileAttributes.ReparsePoint) != 0)
								conflicts++;
							else if (item.Type == FtpObjectType.Directory && Directory.Exists(existing))
								await SyncFolderAsync(existing, item.FullName);
							else if (item.Type != FtpObjectType.File || !File.Exists(existing) ||
								new FileInfo(existing).Length != item.Size)
								conflicts++;
						}
						else if (item.Type == FtpObjectType.Directory)
						{
							Directory.CreateDirectory(target);
							await SyncFolderAsync(target, item.FullName);
						}
						else
						{
							var temporary = Path.Combine(localFolder, $".paco-{Guid.NewGuid():N}.download");
							try
							{
								var result = await client.DownloadFile(temporary, item.FullName,
									FtpLocalExists.Overwrite, FtpVerify.None, ShowRemoteProgress(target, token), token);
								token.ThrowIfCancellationRequested();
								if (result != FtpStatus.Success)
									throw new IOException($"Could not download {item.FullName}.");
								File.Move(temporary, target);
								copied++;
							}
							finally
							{
								File.Delete(temporary);
							}
						}
					}

					foreach (var (name, path) in localItems)
					{
						token.ThrowIfCancellationRequested();
						if (remoteItems.Contains(name))
							continue;
						var attributes = File.GetAttributes(path);
						if ((attributes & FileAttributes.ReparsePoint) != 0 || !CanSyncName(name))
						{
							conflicts++;
							continue;
						}
						var target = remoteFolder.TrimEnd('/') + "/" + name;
						if ((attributes & FileAttributes.Directory) != 0)
						{
							await client.CreateDirectory(target, token);
							await SyncFolderAsync(path, target);
						}
						else
						{
							var result = await client.UploadFile(path, target, FtpRemoteExists.Skip, false,
								FtpVerify.None, ShowRemoteProgress(target, token), token);
							if (result != FtpStatus.Success)
								throw new IOException($"Could not upload {target}.");
							copied++;
						}
					}
				}
			});
		}

		private static bool CanSyncName(string name) =>
			!string.IsNullOrEmpty(name) && name is not "." and not ".." &&
			name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && !name.EndsWith(' ');
	}
}
