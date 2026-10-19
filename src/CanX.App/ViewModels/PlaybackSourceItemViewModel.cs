// SPDX-License-Identifier: GPL-3.0-or-later

using CanX.MediaAccess;

namespace CanX.App.ViewModels;

public sealed class PlaybackSourceItemViewModel
{
    public PlaybackSourceItemViewModel(MediaSourceConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Id = profile.Id;
        Name = profile.Name;
        Protocol = profile.Protocol;
        Type = profile.Protocol switch
        {
            MediaSourceProtocol.LocalFolder => "本地文件夹",
            MediaSourceProtocol.WebDav => "WebDAV",
            MediaSourceProtocol.Ftp => "FTP / FTPS",
            MediaSourceProtocol.Smb => "SMB",
            _ => profile.Protocol.ToString(),
        };
        Location = profile.Protocol == MediaSourceProtocol.LocalFolder
            ? profile.Endpoint.LocalPath
            : profile.Endpoint.AbsoluteUri;
    }

    public string Id { get; }

    public string Name { get; }

    public MediaSourceProtocol Protocol { get; }

    public string Type { get; }

    public string Location { get; }

    public bool IsLocalFolder => Protocol == MediaSourceProtocol.LocalFolder;

    public string? LocalPath => IsLocalFolder ? Location : null;
}
