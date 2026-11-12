using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;

namespace SemanticKG;

public sealed partial class MainWindow
{
    private async Task NewMapAsync()
    {
        if (_busy)
            return;

        _busy = true;
        try
        {
            var title = await AskNameAsync("Новая карта", "Название карты", "Создать");
            if (title is not null && await CanLeaveMapAsync())
                LoadMap(new MindMap { Title = title }, null);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task OpenMapAsync()
    {
        if (_busy)
            return;

        _busy = true;
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id) { Title = "Открыть карту" };
            picker.FileTypeFilter.Add(".skg");
            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            var map = await MindMap.OpenAsync(file.Path);
            if (await CanLeaveMapAsync())
            {
                // Saving the current map may have updated the same file just selected in the picker.
                if (string.Equals(_path, file.Path, StringComparison.OrdinalIgnoreCase))
                    map = await MindMap.OpenAsync(file.Path);
                LoadMap(map, file.Path);
            }
        }
        catch (Exception error) when (IsFileError(error))
        {
            var message = error is JsonException ? "Формат файла карты повреждён." : error.Message;
            await Dialog("Не удалось открыть карту", message, close: "Закрыть").ShowAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SaveMapAsync()
    {
        if (_busy)
            return;

        _busy = true;
        try
        {
            await SaveCoreAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<bool> SaveCoreAsync()
    {
        try
        {
            var path = _path;
            if (path is null)
            {
                var invalid = Path.GetInvalidFileNameChars();
                var name = string.Concat(_map.Title.Select(character => invalid.Contains(character) ? '_' : character)).TrimEnd('.', ' ');
                var picker = new FileSavePicker(AppWindow.Id)
                {
                    Title = "Сохранить карту",
                    SuggestedFileName = name.Length == 0 ? "Карта" : name,
                    DefaultFileExtension = ".skg"
                };
                picker.FileTypeChoices.Add("Карта SemanticKG", [".skg"]);
                var file = await picker.PickSaveFileAsync();
                if (file is null)
                    return false;

                path = file.Path;
            }

            var revision = _revision;
            await _map.SaveAsync(path);
            _path = path;
            _dirty = revision != _revision;
            _isExample = false;
            UpdateFileStatus();
            return !_dirty;
        }
        catch (Exception error) when (IsFileError(error))
        {
            await Dialog("Не удалось сохранить карту", error.Message, close: "Закрыть").ShowAsync();
            return false;
        }
    }

    private async Task<bool> CanLeaveMapAsync()
    {
        if (!_dirty)
            return true;

        var dialog = Dialog("Сохранить изменения?", $"В карте «{_map.Title}» есть несохранённые изменения.", "Сохранить", "Отмена");
        dialog.SecondaryButtonText = "Не сохранять";
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Secondary || (result == ContentDialogResult.Primary && await SaveCoreAsync());
    }

    private void LoadMap(MindMap map, string? path)
    {
        _map = map;
        _path = path;
        _isExample = false;
        _dirty = false;
        _selectedNode = map.Nodes.FirstOrDefault();
        _selectedLink = null;
        _focusNodeId = null;
        _sourceId = null;
        _connecting = false;
        _tagFilter = null;
        SearchField.Text = "";
        Refresh();
        Graph.Fit();
        UpdateFileStatus();
    }

    private void MarkChanged()
    {
        _revision++;
        _dirty = true;
        UpdateFileStatus();
    }

    private void UpdateFileStatus()
    {
        Title = $"SemanticKG — {_map.Title}";
        StatusDot.Fill = GraphView.Paint(_dirty ? 0xF5C981 : 0xAD99F5);
        StatusText.Text = _dirty ? "Несохранённые изменения" : _isExample ? "Демонстрационная карта" :
            _path is null ? "Новая карта" : Path.GetFileName(_path);
        ToolTipService.SetToolTip(StatusText, _path);
    }

    private ContentDialog Dialog(string title, object content, string primary = "", string close = "") => new()
    {
        XamlRoot = Root.XamlRoot,
        RequestedTheme = ElementTheme.Dark,
        Title = title,
        Content = content is string text ? new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } : content,
        PrimaryButtonText = primary,
        CloseButtonText = close,
        DefaultButton = primary.Length == 0 ? ContentDialogButton.Close : ContentDialogButton.Primary
    };

    private async Task<string?> AskNameAsync(string title, string label, string accept)
    {
        var field = new TextBox { Header = label, MaxLength = MindMap.TitleLimit, MinWidth = 250 };
        var dialog = Dialog(title, field, accept, "Отмена");
        dialog.IsPrimaryButtonEnabled = false;
        var accepted = false;
        field.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(field.Text);
        field.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Enter || !dialog.IsPrimaryButtonEnabled)
                return;

            // A focused TextBox handles Enter before the dialog's default button can receive it.
            args.Handled = true;
            accepted = true;
            dialog.Hide();
        };
        dialog.Opened += (_, _) => field.Focus(FocusState.Programmatic);
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary || accepted ? field.Text.Trim() : null;
    }

    private async void WindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closing || (!_busy && !_dirty))
            return;

        args.Cancel = true;
        if (_busy)
            return;

        _busy = true;
        try
        {
            if (await CanLeaveMapAsync())
            {
                _closing = true;
                Close();
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private static bool IsFileError(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or COMException or ArgumentException;

    private async void SaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SaveMapAsync();
    }

    private async void OpenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await OpenMapAsync();
    }

    private async void NewMapClicked(object sender, RoutedEventArgs args) => await NewMapAsync();
    private async void OpenMapClicked(object sender, RoutedEventArgs args) => await OpenMapAsync();
    private async void SaveMapClicked(object sender, RoutedEventArgs args) => await SaveMapAsync();
}
