using System;

namespace Genie.Core.Diagnostics;

/// <summary>
/// Privacy-safe reporting for a failed <c>Process.Start(url)</c> handoff to the
/// user's browser (#307).
///
/// <para>Two things must never reach a user-visible line, because users copy
/// those lines into public issue reports — which is exactly how #279 was filed
/// with a local filesystem path in it:</para>
/// <list type="bullet">
///   <item><b>The launch exception's message.</b> .NET builds it as
///   <c>"An error occurred trying to start process '&lt;FileName&gt;' with working
///   directory '&lt;cwd&gt;'"</c> — so it embeds BOTH the full URL (for a
///   GitHub prefill link, that is the entire drafted issue body) and the working
///   directory, which on Windows contains the user's account name.</item>
///   <item><b>A long prefill URL.</b> Echoing several kilobytes of
///   percent-encoded issue body into the game window reads as corruption and is
///   useless to paste by hand.</item>
/// </list>
///
/// <para>Callers report <see cref="SafeReason"/> instead of the message, and
/// <see cref="ShortUrl"/> instead of the raw URL.</para>
/// </summary>
public static class BrowserLaunch
{
    /// <summary>Longest URL still worth echoing in full. Ordinary help links
    /// (Elanthipedia, the wiki, a bare repo URL) sit far under this; GitHub
    /// new-issue prefill URLs carry the whole encoded body and run to several
    /// thousand characters.</summary>
    public const int MaxEchoUrlChars = 200;

    /// <summary>
    /// A one-line reason for a failed launch that carries no path and no URL —
    /// the exception TYPE plus, for a Win32 error, its numeric code. That is
    /// enough to tell "no browser registered" from "shell refused it" in a bug
    /// report, without leaking the machine.
    /// </summary>
    public static string SafeReason(Exception ex) => ex switch
    {
        System.ComponentModel.Win32Exception w32 =>
            $"the OS shell refused the request (Win32 error {w32.NativeErrorCode})",
        System.IO.FileNotFoundException => "no default browser is registered",
        UnauthorizedAccessException     => "access was denied",
        _                               => ex.GetType().Name,
    };

    /// <summary>
    /// The URL if it is short enough to retype, otherwise its scheme + host +
    /// path with the query dropped. A dropped query is marked so the user knows
    /// the link is truncated rather than broken, and <paramref name="hint"/>
    /// says where the full content went.
    /// </summary>
    public static string ShortUrl(string url, string? hint = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (url.Length <= MaxEchoUrlChars)  return url;

        var cut  = url.IndexOf('?');
        var stem = cut > 0 ? url[..cut] : url[..MaxEchoUrlChars];
        return hint is { Length: > 0 }
            ? $"{stem} (the prefilled details are too long to print — {hint})"
            : $"{stem} (prefilled details omitted — too long to print)";
    }
}
