// Copyright (c) Files Community
// Licensed under the MIT License.

using FluentFTP;
using FluentFTP.Exceptions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Directory = System.IO.Directory;
using File = System.IO.File;
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
			FtpListItem[] items;
			try
			{
				items = await client.GetListing(directory, token);
			}
			catch (FtpCommandException exception) when (exception.CompletionCode is "500" or "502")
			{
				items = await client.GetListing(directory, FtpListOption.ForceList, token);
			}
			token.ThrowIfCancellationRequested();
			_listedRemoteDirectory = directory;
			RemoteDirectory.Text = directory;
			RemoteEntries.ItemsSource = items;
			RemoteStatus.Text = $"{items.Length} items in {directory}";
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

				if (string.IsNullOrEmpty(item.Name) || item.Name is "." or ".." ||
					item.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
					item.Name.EndsWith('.') || item.Name.EndsWith(' '))
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
