// XonarU1: manual update check against the project's GitHub releases.
// Copyright (C) 2026 XonarU1 contributors. Licensed under GPL-3.0-or-later.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;

static partial class XonarU1
{
    const string RepoUrl = "https://github.com/Igloczek/asus-xonar-u1-windows";
    const int UpdateTimeoutMs = 10000;

    // /releases/latest redirects to the newest tag, which needs no API or JSON.
    static string LatestTag()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var request = (HttpWebRequest)WebRequest.Create(RepoUrl + "/releases/latest");
        request.Method = "HEAD";
        request.AllowAutoRedirect = false;
        request.UserAgent = "XonarU1";
        request.Timeout = UpdateTimeoutMs;
        using (var response = request.GetResponse())
        {
            string location = response.Headers["Location"];
            return location.Substring(location.LastIndexOf('/') + 1);
        }
    }

    static bool IsNewer(string tag)
    {
        return new System.Version(tag.TrimStart('v')) > new System.Version(Version);
    }

    // The setup file replaces this copy and restarts it, like a manual update.
    static void DownloadAndRunSetup(string tag)
    {
        string setup = Path.Combine(Path.GetTempPath(), "XonarU1-Setup.exe");
        using (var client = new WebClient())
            client.DownloadFile(RepoUrl + "/releases/download/" + tag + "/XonarU1-Setup.exe", setup);
        Process.Start(setup);
    }
}
