// This code is licensed under the BSD 3-Clause license (see LICENSE.txt for details)

using System;
using System.IO;
using System.Text;

namespace Cosmos.Network.Ftp;

/// <summary>
/// Formats directory entries for <c>LIST</c>, in the <c>ls -l</c> form FTP
/// clients parse, and for <c>NLST</c>, one name per line.
/// </summary>
internal static class FtpListing
{
    /// <summary>
    /// Month abbreviations, spelled out rather than taken from a culture:
    /// clients parse the English ones, whatever the culture the server runs in.
    /// </summary>
    private static readonly string[] s_months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>Appends one <c>LIST</c> line for <paramref name="entry"/>.</summary>
    /// <param name="builder">Where the line goes.</param>
    /// <param name="entry">The file or directory.</param>
    /// <param name="now">The current time, which decides whether the date shows the time or the year, as <c>ls</c> does.</param>
    public static void AppendLong(StringBuilder builder, FileSystemInfo entry, DateTime now)
    {
        bool isDirectory = entry is DirectoryInfo;
        long size = entry is FileInfo file ? file.Length : 0;
        DateTime modified = entry.LastWriteTimeUtc;

        builder.Append(isDirectory ? "drwxr-xr-x" : "-rw-r--r--");
        builder.Append(" 1 owner group ");
        builder.Append(size.ToString().PadLeft(12));
        builder.Append(' ');
        builder.Append(s_months[modified.Month - 1]);
        builder.Append(' ');
        builder.Append(modified.Day.ToString().PadLeft(2));
        builder.Append(' ');

        // ls shows the time for the last six months, the year before that.
        if (modified > now.AddMonths(-6) && modified <= now.AddDays(1))
        {
            builder.Append(modified.Hour.ToString("D2"));
            builder.Append(':');
            builder.Append(modified.Minute.ToString("D2"));
        }
        else
        {
            builder.Append(' ');
            builder.Append(modified.Year.ToString());
        }

        builder.Append(' ');
        builder.Append(entry.Name);
        builder.Append("\r\n");
    }

    /// <summary>Appends one <c>NLST</c> line for <paramref name="entry"/>.</summary>
    public static void AppendName(StringBuilder builder, FileSystemInfo entry)
    {
        builder.Append(entry.Name);
        builder.Append("\r\n");
    }
}
