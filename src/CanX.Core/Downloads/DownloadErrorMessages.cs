// SPDX-License-Identifier: GPL-3.0-or-later

namespace CanX.Core.Downloads;

public static class DownloadErrorMessages
{
    public static string GetMessage(int errorCode)
    {
        if (errorCode <= 0)
        {
            return string.Empty;
        }

        return errorCode switch
        {
            1 => "发生未知错误。",
            2 => "下载超时。",
            3 => "找不到远程资源。",
            4 => "找不到资源，且已达到最大重试次数。",
            5 => "下载速度过低，任务已停止。",
            6 => "发生网络错误。",
            7 => "仍有未完成的下载。",
            8 => "远程服务器不支持断点续传。",
            9 => "磁盘空间不足。",
            10 => "分片长度与控制文件不一致。",
            11 => "另一个任务正在下载同一个文件。",
            12 => "另一个任务正在下载同一个 Torrent。",
            13 => "目标文件已经存在。",
            14 => "无法重命名文件。",
            15 => "无法打开现有文件。",
            16 => "无法创建或截断文件。",
            17 => "发生文件系统错误。",
            18 => "无法创建目录。",
            19 => "域名解析失败。",
            20 => "无法解析 Metalink 文档。",
            21 => "FTP 命令失败。",
            22 => "HTTP 响应头无效或无法识别。",
            23 => "重定向次数过多。",
            24 => "HTTP 授权失败。",
            25 => "无法解析 bencoded 文件。",
            26 => "Torrent 文件损坏或缺少必要信息。",
            27 => "Magnet URI 无效。",
            28 => "选项无效或无法识别。",
            29 => "远程服务器暂时无法处理请求。",
            30 => "JSON-RPC 请求失败。",
            31 => "文件校验失败。",
            _ => $"aria2 错误 {errorCode}。",
        };
    }
}
