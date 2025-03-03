using CsvHelper;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace CellReader;

public sealed partial class MainWindow : Window
{
    private readonly TextBlock placeholderTextBlock;
    private StorageFolder? selectedFolder;
    private bool hasSelectedFiles = false;
    private readonly HashSet<string> availableMarkers = [];
    private readonly Dictionary<string, string> markerNameToType = [];

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool HasSelectedFiles
    {
        get => hasSelectedFiles;
        set
        {
            hasSelectedFiles = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = null!) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public MainWindow()
    {
        InitializeComponent();
        placeholderTextBlock = new TextBlock
        {
            Text = "Please select a folder with CSV files (*.csv)",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.5
        };
        CheckFileList();
        LoadingMarkersSpinner.Visibility = Visibility.Visible;
        LoadingFilesSpinner.Visibility = Visibility.Visible;
        LoadingOutputSpinner.Visibility = Visibility.Visible;
    }

    private void CheckFileList()
    {
        if (FileList.Items.Count == 0)
        {
            FileList.Items.Add(placeholderTextBlock);
            HasSelectedFiles = false;
        }
    }

    private async void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        selectedFolder = await picker?.PickSingleFolderAsync();
        if (selectedFolder == null)
        {
            FileList.Items.Clear();
            CheckFileList();
            return;
        }

        await RenderFilesList();
    }

    private async Task RenderFilesList()
    {
        try
        {
            LoadingFilesSpinner.IsActive = true;
            var files = await selectedFolder!.GetFilesAsync();
            var excelFiles = files.Where(f => f.FileType == ".csv").ToList();

            if (excelFiles.Count == 0)
            {
                await ShowErrorAsync("The selected folder does not contain any Excel files.");
                CheckFileList();
            }
            else
            {
                FileList.Items.Clear();
                foreach (var file in excelFiles)
                {
                    var checkBox = new CheckBox { Content = file.Name };
                    checkBox.Checked += FileSelectionChanged;
                    checkBox.Unchecked += FileSelectionChanged;
                    FileList.Items.Add(checkBox);
                }
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("An error occurred while processing the selected folder: " + ex.Message);
        }
        finally
        {
            LoadingFilesSpinner.IsActive = false;
        }
    }

    private async void GetMarkerTypes_Click(object sender, RoutedEventArgs e)
    {
        LoadingMarkersSpinner.IsActive = true;

        var selectedFiles = FileList.Items.OfType<CheckBox>().Where(cb => cb.IsChecked == true).Select(cb => cb.Content);
        var cellValues = new System.Text.StringBuilder();
        availableMarkers.Clear();
        TypeList.Items.Clear();

        await FetchAvailableMarkers(selectedFiles);
        await ClassifyColumns(selectedFiles);

        LoadingMarkersSpinner.IsActive = false;
    }

    private async Task ClassifyColumns(IEnumerable<object> selectedFiles)
    {
        foreach (var marker in availableMarkers)
        {
            // Determine if the column is a "Type" or a "Marker"
            bool isType = true;
            foreach (var fileName in selectedFiles)
            {
                if (markerNameToType.TryGetValue(marker, out var _))
                {
                    continue;
                }
                var isFileHasMarker = false;
                var file = await selectedFolder?.GetFileAsync(fileName.ToString());
                if (file.FileType == ".csv")
                {
                    using var stream = await file.OpenStreamForReadAsync();
                    using var reader = new StreamReader(stream);
                    using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

                    var records = csv.GetRecords<dynamic>().ToList();
                    if (records.Count > 0)
                    {
                        foreach (var record in records.Take(10)) //TODO: Check if this is a good number.
                        {
                            if (record is IDictionary<string, object> recordDict && recordDict.TryGetValue(marker, out var recordValue))
                            {
                                isFileHasMarker = true;
                                var value = recordValue?.ToString();
                                if (value != "0" && value != "1")
                                {
                                    isType = false;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (isFileHasMarker)
                {
                    markerNameToType.Add(marker, isType ? "Type" : "Marker");
                    break;
                }
            }

            var stackPanel = new StackPanel { Orientation = Orientation.Horizontal };
            var checkBox = new CheckBox { Content = marker, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            RenderMarkerLine(stackPanel, checkBox, isType);
        }
    }

    private void RenderMarkerLine(StackPanel stackPanel, CheckBox checkBox, bool isType)
    {
        if (isType)
        {
            var toggleSwitch = new ToggleSwitch { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            stackPanel.Children.Add(checkBox);
            stackPanel.Children.Add(toggleSwitch);
        }
        else
        {
            var minTextBox = new TextBox { Width = 50, Margin = new Thickness(0, 0, 10, 0), PlaceholderText = "Min" };
            var maxTextBox = new TextBox { Width = 50, PlaceholderText = "Max" };
            stackPanel.Children.Add(checkBox);
            stackPanel.Children.Add(minTextBox);
            stackPanel.Children.Add(maxTextBox);
        }

        TypeList.Items.Add(stackPanel);
    }

    private async Task FetchAvailableMarkers(IEnumerable<object> selectedFiles)
    {
        foreach (var fileName in selectedFiles)
        {
            var file = await selectedFolder?.GetFileAsync(fileName.ToString());
            if (file.FileType == ".csv")
            {
                using var stream = await file.OpenStreamForReadAsync();
                using var reader = new StreamReader(stream);
                using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

                var records = csv.GetRecords<dynamic>().ToList();
                if (records.Count > 0)
                {
                    //get the titles from the first record, assuming all records have the same structure
                    if (records[0] is not IDictionary<string, object> firstRecord)
                    {
                        continue;
                    }

                    //TODO: Save 7 into a config.
                    foreach (var key in firstRecord.Keys.Where((k, i) => i > 7))
                    {
                        availableMarkers.Add(key);
                    }
                }
            }
        }
    }

    private async void Calculate_Click(object sender, RoutedEventArgs e)
    {
        OutputText.Text = "Calculating...";
        await Task.Run(() => { Thread.Sleep(2000); });
        OutputText.Text = "Calculating... (Not really)";
    }

    private void SelectAllFiles_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in FileList.Items.OfType<CheckBox>())
        {
            item.IsChecked = true;
        }
        CheckFileList();
    }

    private void ClearFilesSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in FileList.Items.OfType<CheckBox>())
        {
            item.IsChecked = false;
        }
        CheckFileList();
    }

    private void SelectAllMarkers_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in TypeList.Items.OfType<StackPanel>())
        {
            var checkBox = item.Children.OfType<CheckBox>().FirstOrDefault();
            if (checkBox != null)
            {
                checkBox.IsChecked = true;
            }
        }
    }

    private void ClearMarkersSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in TypeList.Items.OfType<StackPanel>())
        {
            var checkBox = item.Children.OfType<CheckBox>().FirstOrDefault();
            if (checkBox != null)
            {
                checkBox.IsChecked = false;
            }
        }
    }

    private void ClearOutput_Click(object sender, RoutedEventArgs e)
    {
        OutputText.Text = "";
    }

    private void FileSelectionChanged(object sender, RoutedEventArgs e)
    {
        HasSelectedFiles = FileList.Items.OfType<CheckBox>().Any(cb => cb.IsChecked == true);
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Error",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot
        };

        _ = await dialog.ShowAsync();
    }
}
