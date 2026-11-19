// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using Windows.ApplicationModel.DataTransfer;

namespace Files.App.Helpers
{
	/// <summary>
	/// Tracks drags between split panes and from the remote browser so drops copy by default.
	/// </summary>
	internal static class PaneDragDropHelpers
	{
		private const string SourcePaneProperty = "PacosFileManager_SourcePane";
		private const string RemoteItemsFormat = "PacosFileManager_RemoteItems";

		private static readonly ConditionalWeakTable<IShellPage, string> PaneIds = new();

		private static string? _remoteDragId;
		private static Action<string>? _remoteDropHandler;

		public static void MarkSourcePane(DataPackage data, IShellPage? shellPage)
		{
			if (GetPane(shellPage) is { } pane)
				data.Properties[SourcePaneProperty] = PaneIds.GetValue(pane, static _ => Guid.NewGuid().ToString("N"));
		}

		public static bool IsFromOtherPane(DataPackageView? view, IShellPage? shellPage)
		{
			if (view is null || GetPane(shellPage) is not { } pane ||
				!view.Properties.TryGetValue(SourcePaneProperty, out var value) || value is not string sourceId)
				return false;

			return !PaneIds.TryGetValue(pane, out var id) || id != sourceId;
		}

		// Columns share their owning pane so moving between columns keeps the default behavior.
		private static IShellPage? GetPane(IShellPage? shellPage) =>
			shellPage is ColumnShellPage column
				? column.FindAscendant<ColumnsLayoutPage>()?.ParentShellPageInstance ?? column
				: shellPage;

		public static void BeginRemoteDrag(DataPackage data, Action<string> dropHandler)
		{
			_remoteDragId = Guid.NewGuid().ToString("N");
			_remoteDropHandler = dropHandler;
			data.SetData(RemoteItemsFormat, _remoteDragId);
			data.RequestedOperation = DataPackageOperation.Copy;
		}

		public static void EndRemoteDrag()
		{
			_remoteDragId = null;
			_remoteDropHandler = null;
		}

		public static bool HasRemoteItems(DataPackageView? view) =>
			view is not null && view.Contains(RemoteItemsFormat);

		/// <summary>
		/// Starts downloading the dragged remote items into a local folder.
		/// </summary>
		/// <returns>True when the data came from the remote browser, whether or not the drop was accepted.</returns>
		public static async Task<bool> TryDropRemoteItemsAsync(DataPackageView? view, string? destination)
		{
			if (!HasRemoteItems(view))
				return false;

			var handler = _remoteDropHandler;
			var expectedId = _remoteDragId;
			if (handler is null || expectedId is null || string.IsNullOrEmpty(destination))
				return true;

			try
			{
				if (await view!.GetDataAsync(RemoteItemsFormat) is string id && id == expectedId)
					handler(destination);
			}
			catch (Exception ex)
			{
				// Drop handlers are async void, so a failed read must not escape
				App.Logger.LogWarning(ex, "Could not read the dragged remote items.");
			}

			return true;
		}
	}
}
