// Copyright (c) Files Community
// Licensed under the MIT License.

using FluentFTP;
using FluentFTP.Exceptions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Directory = System.IO.Directory;
using DirectoryInfo = System.IO.DirectoryInfo;
using File = System.IO.File;
using FileAttributes = System.IO.FileAttributes;
using IOException = System.IO.IOException;
using Path = System.IO.Path;

namespace Files.App.Views
{
	public sealed partial class ShellPanesPage
	{
		private AsyncFtpClient? _remoteClient;
		private CancellationTokenSource? _remoteOperation;
		private string _listedRemoteDirectory = "/";
		private bool _remoteDisposed;

		private async void RemoteConnect_Click(object sender, RoutedEventArgs e)
		{
			if (_remoteClient is not null)
			{
				DisconnectRemote();
				RemoteStatus.Text = "Disconnected.";
				return;
			}

			var pane = ActivePaneOrColumn;
			var host = RemoteHost.Text.Trim();
			var encryption = RemoteProtocol.SelectedIndex switch
			{
				1 => FtpEncryptionMode.Explicit,
				2 => FtpEncryptionMode.Implicit,
				_ => FtpEncryptionMode.None,
			};
			int port = encryption == FtpEncryptionMode.Implicit ? 990 : 21;
			if (Uri.CheckHostName(host) == UriHostNameType.Unknown ||
				(!string.IsNullOrWhiteSpace(RemotePort.Text) &&
				(!int.TryParse(RemotePort.Text, out port) || port is < 1 or > 65535)) ||
				string.IsNullOrWhiteSpace(RemoteUser.Text))
			{
				RemoteStatus.Text = "Enter a valid host, port, and username.";
				return;
			}

			await RunRemoteOperationAsync(async token =>
			{
				if (encryption == FtpEncryptionMode.None &&
					!await ConfirmRemoteAsync("Use plain FTP?", "The server will receive your password and files without encryption.", "Connect"))
					return;

				var client = new AsyncFtpClient(host, RemoteUser.Text, RemotePassword.Password, port);
				client.Config.EncryptionMode = encryption;
				client.Config.DataConnectionEncryption = encryption != FtpEncryptionMode.None;
				client.Config.ValidateAnyCertificate = false;
				// Use classic passive mode with the connected host's address for IPv4 servers.
				if (Uri.CheckHostName(host) != UriHostNameType.IPv6)
					client.Config.DataConnectionType = FtpDataConnectionType.PASVEX;
				try
				{
					RemoteStatus.Text = $"Connecting to {host}:{port}…";
					try
					{
						await client.Connect(token);
					}
					catch (FtpCommandException exception)
					{
						throw new IOException($"Server rejected connection setup: {exception.Message}", exception);
					}
					await ListRemoteAsync(client, FtpStartPath.Text.Trim(), token);
					var url = FtpUrl(host, port, encryption, _listedRemoteDirectory);
					FtpManager.SiteCredentials[FtpManager.SiteKey(host, port, encryption)] =
						new System.Net.NetworkCredential(RemoteUser.Text, RemotePassword.Password);
					pane?.NavigateToPath(url);
					_remoteClient = client;
					RemotePassword.Password = string.Empty;
					RemoteConnect.Content = "Disconnect";
					RemoteConnectionStatus.Text = "Connected";
					RemoteBrowser.Visibility = Visibility.Visible;
				}
				finally
				{
					if (!ReferenceEquals(_remoteClient, client))
						client.Dispose();
				}
			});
		}

		private async void RemoteList_Click(object sender, RoutedEventArgs e)
		{
			if (_remoteClient is { } client)
				await RunRemoteOperationAsync(token => ListRemoteAsync(client, RemoteDirectory.Text.Trim(), token));
		}

		private async void RemoteOpen_Click(object sender, RoutedEventArgs e)
		{
			if (_remoteClient is { } client && RemoteEntries.SelectedItem is FtpListItem { Type: FtpObjectType.Directory } item)
				await RunRemoteOperationAsync(token => ListRemoteAsync(client, item.FullName, token));
		}

		private async Task ListRemoteAsync(AsyncFtpClient client, string directory, CancellationToken token)
		{
			if (!directory.StartsWith('/') || directory.Contains('\r') || directory.Contains('\n'))
				throw new ArgumentException("Enter an absolute remote folder path beginning with /.");

			await client.SetWorkingDirectory(directory, token);
			var items = await GetRemoteListingAsync(client, directory, token);
			token.ThrowIfCancellationRequested();
			_listedRemoteDirectory = directory;
			RemoteDirectory.Text = directory;
			RemoteEntries.ItemsSource = items;
			RemoteStatus.Text = $"{items.Length} items in {directory}";
		}

		private static async Task<FtpListItem[]> GetRemoteListingAsync(AsyncFtpClient client, string directory, CancellationToken token)
		{
			try
			{
				return await client.GetListing(directory, token);
			}
			catch (FtpCommandException exception) when (exception.CompletionCode is "500" or "502")
			{
				return await client.GetListing(directory, FtpListOption.ForceList, token);
			}
		}

		private async void RemoteUpload_Click(object sender, RoutedEventArgs e)
		{
			if (_remoteClient is not { } client)
				return;

			await RunRemoteOperationAsync(async token =>
			{
				var picker = new FileOpenPicker();
				picker.FileTypeFilter.Add("*");
				WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
				var file = await picker.PickSingleFileAsync();
				if (file is null)
					return;

				token.ThrowIfCancellationRequested();
				var destination = _listedRemoteDirectory.TrimEnd('/') + "/" + file.Name;
				var overwrite = await client.FileExists(destination, token);
				if (overwrite && !await ConfirmRemoteAsync("Replace remote file?", destination, "Replace"))
					return;

				token.ThrowIfCancellationRequested();
				var progress = ShowRemoteProgress(destination, token);
				var status = await client.UploadFile(file.Path, destination,
					overwrite ? FtpRemoteExists.Overwrite : FtpRemoteExists.Skip,
					false, FtpVerify.None, progress, token);
				if (status != FtpStatus.Success)
					throw new IOException($"Upload returned {status} for {destination}.");

				await ListRemoteAsync(client, _listedRemoteDirectory, token);
				RemoteStatus.Text = $"Uploaded to {destination}";
			});
		}

		private async void RemoteDownload_Click(object sender, RoutedEventArgs e)
		{
			if (_remoteClient is not { } client || RemoteEntries.SelectedItem is not FtpListItem { Type: FtpObjectType.File } item)
			{
				RemoteStatus.Text = "Select a remote file to download.";
				return;
			}

			await RunRemoteOperationAsync(async token =>
			{
				var folder = ActivePaneOrColumn?.ShellViewModel?.WorkingDirectory;
				if (!Directory.Exists(folder))
					throw new IOException("Open a local folder in the active pane before downloading.");

				if (!IsValidLocalName(item.Name))
					throw new IOException("The selected file name is invalid on Windows.");

				var destination = Path.Combine(folder, item.Name);
				var overwrite = File.Exists(destination);
				if (overwrite && !await ConfirmRemoteAsync("Replace local file?", destination, "Replace"))
					return;

				token.ThrowIfCancellationRequested();
				var temporaryPath = Path.Combine(folder, $".paco-{Guid.NewGuid():N}.download");
				try
				{
					var status = await client.DownloadFile(temporaryPath, item.FullName,
						FtpLocalExists.Overwrite, FtpVerify.None, ShowRemoteProgress(destination, token), token);
					token.ThrowIfCancellationRequested();
					if (status != FtpStatus.Success)
						throw new IOException($"Download returned {status} for {destination}.");

					File.Move(temporaryPath, destination, overwrite);
					RemoteStatus.Text = $"Downloaded to {destination}";
				}
				finally
				{
					File.Delete(temporaryPath);
				}
			});
		}

		private static bool IsValidLocalName(string? name) =>
			!string.IsNullOrEmpty(name) && name is not "." and not ".." &&
			name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
			!name.EndsWith('.') && !name.EndsWith(' ');

		private void RemoteEntries_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
		{
			var items = e.Items.OfType<FtpListItem>()
				.Where(item => item.Type is FtpObjectType.File or FtpObjectType.Directory && item.Name is not "." and not "..")
				.ToList();
			if (_remoteClient is null || _remoteOperation is not null || items.Count == 0)
			{
				e.Cancel = true;
				return;
			}

			PaneDragDropHelpers.BeginRemoteDrag(e.Data, folder => DownloadRemoteItems(items, folder));
		}

		private void RemoteEntries_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
			PaneDragDropHelpers.EndRemoteDrag();

		private async void DownloadRemoteItems(IReadOnlyList<FtpListItem> items, string folder)
		{
			if (_remoteClient is not { } client)
			{
				RemoteStatus.Text = "Connect to a server before downloading.";
				return;
			}

			await RunRemoteOperationAsync(async token =>
			{
				if (!Directory.Exists(folder))
					throw new IOException("Drop remote items onto a local folder.");

				int downloaded = 0;
				foreach (var item in items)
				{
					token.ThrowIfCancellationRequested();
					if (!IsValidLocalName(item.Name))
						throw new IOException($"{item.Name} is not a valid name on Windows.");

					var destination = Path.Combine(folder, item.Name);
					if (item.Type is FtpObjectType.Directory)
					{
						if (File.Exists(destination))
							throw new IOException($"A file named {item.Name} already exists in {folder}.");
						if (Directory.Exists(destination) &&
							!await ConfirmRemoteAsync("Merge into local folder?", $"{destination}\nFiles with the same name will be replaced.", "Merge"))
							continue;

						await DownloadRemoteFolderAsync(client, item.FullName, destination, token);
					}
					else
					{
						if (Directory.Exists(destination))
							throw new IOException($"A folder named {item.Name} already exists in {folder}.");
						if (File.Exists(destination) && !await ConfirmRemoteAsync("Replace local file?", destination, "Replace"))
							continue;

						await DownloadRemoteFileAsync(client, item.FullName, destination, token);
					}
					downloaded++;
				}

				RemoteStatus.Text = $"Downloaded {downloaded} of {items.Count} items to {folder}";
			});
		}

		// Walks the folder here instead of using DownloadDirectory so every name is checked before it touches the disk.
		private async Task DownloadRemoteFolderAsync(AsyncFtpClient client, string remoteFolder, string localFolder, CancellationToken token)
		{
			Directory.CreateDirectory(localFolder);
			foreach (var entry in await GetRemoteListingAsync(client, remoteFolder, token))
			{
				token.ThrowIfCancellationRequested();
				if (entry.Name is "." or ".." || entry.Type is not (FtpObjectType.File or FtpObjectType.Directory))
					continue;
				if (!IsValidLocalName(entry.Name))
					throw new IOException($"{entry.FullName} is not a valid name on Windows.");

				var destination = Path.Combine(localFolder, entry.Name);
				if (entry.Type is FtpObjectType.Directory)
					await DownloadRemoteFolderAsync(client, entry.FullName, destination, token);
				else
					await DownloadRemoteFileAsync(client, entry.FullName, destination, token);
			}
		}

		private async Task DownloadRemoteFileAsync(AsyncFtpClient client, string remotePath, string destination, CancellationToken token)
		{
			var folder = Path.GetDirectoryName(destination)
				?? throw new IOException($"{destination} has no parent folder.");
			var temporaryPath = Path.Combine(folder, $".paco-{Guid.NewGuid():N}.download");
			try
			{
				var status = await client.DownloadFile(temporaryPath, remotePath,
					FtpLocalExists.Overwrite, FtpVerify.None, ShowRemoteProgress(destination, token), token);
				token.ThrowIfCancellationRequested();
				if (status != FtpStatus.Success)
					throw new IOException($"Download returned {status} for {destination}.");

				// Replacing an existing file was confirmed before the transfer started.
				File.Move(temporaryPath, destination, true);
			}
			finally
			{
				File.Delete(temporaryPath);
			}
		}

		private string RemoteDropFolder(DragEventArgs e) =>
			(e.OriginalSource as FrameworkElement)?.DataContext is FtpListItem { Type: FtpObjectType.Directory } folder
				? folder.FullName
				: _listedRemoteDirectory;

		private void RemoteEntries_DragOver(object sender, DragEventArgs e)
		{
			if (_remoteClient is null || _remoteOperation is not null || !global::Files.App.Utils.Storage.FilesystemHelpers.HasDraggedStorageItems(e.DataView))
			{
				e.AcceptedOperation = DataPackageOperation.None;
				return;
			}

			e.Handled = true;
			e.AcceptedOperation = DataPackageOperation.Copy;
			e.DragUIOverride.IsCaptionVisible = true;
			e.DragUIOverride.Caption = $"Upload to {RemoteDropFolder(e)}";
		}

		private async void RemoteEntries_Drop(object sender, DragEventArgs e)
		{
			if (_remoteClient is not { } client || !global::Files.App.Utils.Storage.FilesystemHelpers.HasDraggedStorageItems(e.DataView))
				return;

			e.Handled = true;
			var remoteFolder = RemoteDropFolder(e);
			List<string> paths;
			var deferral = e.GetDeferral();
			try
			{
				paths = (await global::Files.App.Utils.Storage.FilesystemHelpers.GetDraggedStorageItems(e.DataView))
					.Select(item => item.Path)
					.Where(path => !string.IsNullOrEmpty(path) && Path.IsPathRooted(path) && (File.Exists(path) || Directory.Exists(path)))
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToList();
			}
			catch (Exception exception)
			{
				RemoteStatus.Text = exception.Message;
				return;
			}
			finally
			{
				deferral.Complete();
			}

			if (paths.Count == 0)
			{
				RemoteStatus.Text = "Only local files and folders can be uploaded.";
				return;
			}

			await RunRemoteOperationAsync(async token =>
			{
				int uploaded = 0;
				foreach (var path in paths)
				{
					token.ThrowIfCancellationRequested();
					var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
					if (string.IsNullOrEmpty(name))
						continue;

					var destination = remoteFolder.TrimEnd('/') + "/" + name;
					if (Directory.Exists(path))
					{
						if (await client.FileExists(destination, token))
							throw new IOException($"A remote file named {name} already exists in {remoteFolder}.");
						if (await client.DirectoryExists(destination, token) &&
							!await ConfirmRemoteAsync("Merge into remote folder?", $"{destination}\nFiles with the same name will be replaced.", "Merge"))
							continue;

						await UploadLocalFolderAsync(client, path, destination, token);
					}
					else
					{
						if (await client.DirectoryExists(destination, token))
							throw new IOException($"A remote folder named {name} already exists in {remoteFolder}.");
						if (await client.FileExists(destination, token) && !await ConfirmRemoteAsync("Replace remote file?", destination, "Replace"))
							continue;

						await UploadLocalFileAsync(client, path, destination, token);
					}
					uploaded++;
				}

				await ListRemoteAsync(client, _listedRemoteDirectory, token);
				RemoteStatus.Text = $"Uploaded {uploaded} of {paths.Count} items to {remoteFolder}";
			});
		}

		private async Task UploadLocalFolderAsync(AsyncFtpClient client, string localFolder, string remoteFolder, CancellationToken token)
		{
			if (!await client.DirectoryExists(remoteFolder, token) && !await client.CreateDirectory(remoteFolder, token))
				throw new IOException($"Could not create {remoteFolder}.");

			foreach (var entry in new DirectoryInfo(localFolder).EnumerateFileSystemInfos())
			{
				token.ThrowIfCancellationRequested();
				// Junctions and symbolic links can loop or point outside the dropped folder.
				if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
					continue;

				var destination = remoteFolder.TrimEnd('/') + "/" + entry.Name;
				if (entry is DirectoryInfo)
					await UploadLocalFolderAsync(client, entry.FullName, destination, token);
				else
					await UploadLocalFileAsync(client, entry.FullName, destination, token);
			}
		}

		private async Task UploadLocalFileAsync(AsyncFtpClient client, string localPath, string destination, CancellationToken token)
		{
			// Replacing an existing file was confirmed before the transfer started.
			var status = await client.UploadFile(localPath, destination, FtpRemoteExists.Overwrite,
				false, FtpVerify.None, ShowRemoteProgress(destination, token), token);
			if (status != FtpStatus.Success)
				throw new IOException($"Upload returned {status} for {destination}.");
		}

		private async Task<bool> ConfirmRemoteAsync(string title, string message, string action)
		{
			var dialog = new ContentDialog
			{
				XamlRoot = XamlRoot,
				Title = title,
				Content = message,
				PrimaryButtonText = action,
				CloseButtonText = "Cancel",
				DefaultButton = ContentDialogButton.Close,
			};
			return await dialog.ShowAsync() == ContentDialogResult.Primary;
		}

		private IProgress<FtpProgress> ShowRemoteProgress(string destination, CancellationToken token)
		{
			RemoteProgress.Value = 0;
			RemoteProgress.Visibility = Visibility.Visible;
			RemoteStatus.Text = $"Transferring to {destination}";
			RemoteTransferDetails.Text = Path.GetFileName(destination);
			RemoteTransferDetails.Visibility = Visibility.Visible;
			return new Progress<FtpProgress>(progress =>
			{
				if (_remoteOperation?.Token != token || token.IsCancellationRequested)
					return;

				RemoteTransferDetails.Text = $"{Path.GetFileName(destination)} — {progress.TransferSpeedToString()}";
				RemoteProgress.IsIndeterminate = progress.Progress < 0;
				if (progress.Progress >= 0)
					RemoteProgress.Value = progress.Progress;
			});
		}

		private async Task RunRemoteOperationAsync(Func<CancellationToken, Task> operation)
		{
			if (_remoteOperation is not null || _remoteDisposed)
				return;

			using var cancellation = new CancellationTokenSource();
			_remoteOperation = cancellation;
			RemoteConnect.IsEnabled = false;
			RemoteBrowser.IsHitTestVisible = false;
			RemoteSettings.IsHitTestVisible = false;
			RemoteCancel.IsEnabled = true;
			try
			{
				await operation(cancellation.Token);
			}
			catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
			{
				RemoteStatus.Text = "Operation canceled. Use the same action to retry.";
			}
			catch (Exception exception)
			{
				RemoteStatus.Text = exception.Message;
			}
			finally
			{
				_remoteOperation = null;
				RemoteConnect.IsEnabled = true;
				RemoteBrowser.IsHitTestVisible = true;
				RemoteSettings.IsHitTestVisible = true;
				RemoteCancel.IsEnabled = false;
				RemoteProgress.Visibility = Visibility.Collapsed;
				RemoteTransferDetails.Visibility = Visibility.Collapsed;
				RemoteProgress.IsIndeterminate = false;
				if (_remoteDisposed)
					DisconnectRemote();
			}
		}

		private void RemoteCancel_Click(object sender, RoutedEventArgs e) => _remoteOperation?.Cancel();

		private void CloseRemoteConnection()
		{
			_remoteDisposed = true;
			if (_remoteOperation is { } operation)
				operation.Cancel();
			else
				DisconnectRemote();
		}

		private void DisconnectRemote()
		{
			_remoteClient?.Dispose();
			_remoteClient = null;
			RemoteEntries.ItemsSource = null;
			RemoteConnect.Content = "Connect";
			RemoteConnectionStatus.Text = "Disconnected";
			RemoteBrowser.Visibility = Visibility.Collapsed;
		}
	}
}
