// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace Files.App.Views
{
	public sealed partial class ShellPanesPage
	{
		private async void ImportFileZillaSites_Click(object sender, RoutedEventArgs e)
		{
			await RunRemoteOperationAsync(async token =>
			{
				var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
					"FileZilla", "sitemanager.xml");
				if (!File.Exists(path))
				{
					RemoteStatus.Text = "No FileZilla Site Manager file was found.";
					return;
				}

				var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit };
				using var reader = XmlReader.Create(path, settings);
				var document = await XDocument.LoadAsync(reader, LoadOptions.None, token);
				int imported = 0;
				int skipped = 0;
				foreach (var server in document.Descendants("Server"))
				{
					var name = ((string?)server.Element("Name"))?.Trim();
					var host = ((string?)server.Element("Host"))?.Trim();
					var user = (string?)server.Element("User");
					var protocol = ((string?)server.Element("Protocol")) switch
					{
						"0" or "4" => 1, // FTP with optional TLS or explicit TLS
						"3" => 2, // Implicit TLS
						"6" => 3, // Plain FTP
						_ => 0,
					};
					if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(host) ||
						string.IsNullOrWhiteSpace(user) || protocol == 0 ||
						!int.TryParse((string?)server.Element("Port"), out var port) || port is < 1 or > 65535 ||
						_ftpSites.Any(site => site.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
					{
						skipped++;
						continue;
					}

					var folder = ReadFileZillaFolder((string?)server.Element("RemoteDir"));
					_ftpSites.Add(new SavedFtpSite(Guid.NewGuid(), name, host, port, protocol, user, folder));
					imported++;
				}

				if (imported > 0)
					StoreFtpSites();
				RemoteStatus.Text = $"Imported {imported} sites. Skipped {skipped} duplicates or unsupported entries. Select Quick connect, then enter and save the password.";
			});
		}

		private static string ReadFileZillaFolder(string? value)
		{
			if (string.IsNullOrEmpty(value))
				return "/";
			if (ValidFtpFolder(value))
				return value;

			// Site Manager stores Unix paths as a type, a prefix length, then length-prefixed segments.
			if (!value.StartsWith("1 0 ", StringComparison.Ordinal))
				return "/";

			var remaining = value.AsSpan(4);
			var segments = new List<string>();
			while (!remaining.IsEmpty)
			{
				int separator = remaining.IndexOf(' ');
				if (separator < 1 || !int.TryParse(remaining[..separator], out int length) ||
					length < 1 || length > remaining.Length - separator - 1)
					return "/";

				remaining = remaining[(separator + 1)..];
				segments.Add(remaining[..length].ToString());
				remaining = remaining[length..];
				if (!remaining.IsEmpty)
				{
					if (remaining[0] != ' ')
						return "/";
					remaining = remaining[1..];
				}
			}

			var folder = "/" + string.Join("/", segments);
			return ValidFtpFolder(folder) ? folder : "/";
		}
	}
}
