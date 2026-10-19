// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.CompilerServices;
using CanX.MediaAccess;

namespace CanX.App.ViewModels;

public sealed class AddPlaybackSourceViewModel : INotifyPropertyChanged
{
    private readonly MediaSourceBrowserViewModel _mediaSources;
    private PlaybackSourceTypeOption _selectedType;
    private string _name = string.Empty;
    private string _localFolder = string.Empty;
    private string _endpointText = string.Empty;
    private string _userName = string.Empty;
    private string _password = string.Empty;
    private bool _isBusy;

    public AddPlaybackSourceViewModel(MediaSourceBrowserViewModel mediaSources)
    {
        _mediaSources = mediaSources ?? throw new ArgumentNullException(nameof(mediaSources));
        SourceTypes =
        [
            new(MediaSourceProtocol.LocalFolder, "本地文件夹"),
            new(MediaSourceProtocol.WebDav, "WebDAV"),
            new(MediaSourceProtocol.Ftp, "FTP / FTPS"),
            new(MediaSourceProtocol.Smb, "SMB"),
        ];
        _selectedType = SourceTypes[0];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<PlaybackSourceTypeOption> SourceTypes { get; }

    public PlaybackSourceTypeOption SelectedType
    {
        get => _selectedType;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SetField(ref _selectedType, value))
            {
                return;
            }

            EndpointText = value.Protocol switch
            {
                MediaSourceProtocol.WebDav => "https://",
                MediaSourceProtocol.Ftp => "ftp://",
                MediaSourceProtocol.Smb => "smb://",
                _ => string.Empty,
            };
            OnPropertyChanged(nameof(IsLocalFolder));
            OnPropertyChanged(nameof(IsNetworkSource));
            OnPropertyChanged(nameof(EndpointLabel));
            OnPropertyChanged(nameof(EndpointPlaceholder));
            OnPropertyChanged(nameof(SourceDescription));
            OnPropertyChanged(nameof(CanAdd));
        }
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value ?? string.Empty);
    }

    public string LocalFolder
    {
        get => _localFolder;
        set
        {
            if (SetField(ref _localFolder, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanAdd));
            }
        }
    }

    public string EndpointText
    {
        get => _endpointText;
        set
        {
            if (SetField(ref _endpointText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanAdd));
            }
        }
    }

    public string UserName
    {
        get => _userName;
        set => SetField(ref _userName, value ?? string.Empty);
    }

    public string Password
    {
        get => _password;
        set => SetField(ref _password, value ?? string.Empty);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanAdd));
            }
        }
    }

    public bool IsLocalFolder => SelectedType.Protocol == MediaSourceProtocol.LocalFolder;

    public bool IsNetworkSource => !IsLocalFolder;

    public bool CanAdd => !IsBusy && (IsLocalFolder
        ? !string.IsNullOrWhiteSpace(LocalFolder)
        : IsNetworkEndpointReady());

    public string EndpointLabel => SelectedType.Protocol switch
    {
        MediaSourceProtocol.WebDav => "WebDAV 地址",
        MediaSourceProtocol.Ftp => "FTP / FTPS 地址",
        MediaSourceProtocol.Smb => "SMB 服务器 / 共享地址",
        _ => "地址",
    };

    public string EndpointPlaceholder => SelectedType.Protocol switch
    {
        MediaSourceProtocol.WebDav => "https://server.example/dav/",
        MediaSourceProtocol.Ftp => "ftp://server.example/media/",
        MediaSourceProtocol.Smb => "smb://server.example/（共享名可选）",
        _ => string.Empty,
    };

    public string SourceDescription => SelectedType.Protocol switch
    {
        MediaSourceProtocol.LocalFolder => "添加后打开该来源，可浏览文件夹并选择要播放的媒体。",
        MediaSourceProtocol.WebDav => "添加前会连接服务器并读取根目录，以确认地址和凭证可用。",
        MediaSourceProtocol.Ftp => "支持 FTP 和显式 TLS 的 FTPS；添加前会验证连接。",
        MediaSourceProtocol.Smb => "可填写 smb://server/ 浏览服务器共享，也可直接填写 smb://server/share/。",
        _ => string.Empty,
    };

    public async Task AddAsync(CancellationToken cancellationToken = default)
    {
        if (!CanAdd)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (IsLocalFolder)
            {
                await _mediaSources.AddLocalFolderAsync(LocalFolder, Name, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                await _mediaSources.AddNetworkSourceAsync(
                    SelectedType.Protocol,
                    EndpointText,
                    Name,
                    UserName,
                    Password,
                    cancellationToken).ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool IsNetworkEndpointReady()
    {
        if (!Uri.TryCreate(EndpointText.Trim(), UriKind.Absolute, out var endpoint) ||
            string.IsNullOrWhiteSpace(endpoint.Host))
        {
            return false;
        }

        return SelectedType.Protocol switch
        {
            MediaSourceProtocol.WebDav => endpoint.Scheme is "http" or "https",
            MediaSourceProtocol.Ftp => endpoint.Scheme is "ftp" or "ftps",
            MediaSourceProtocol.Smb => endpoint.Scheme == "smb",
            _ => false,
        };
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record PlaybackSourceTypeOption(MediaSourceProtocol Protocol, string Name)
{
    public override string ToString() => Name;
}
