using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Log = System.Console;

namespace Tsul.Network
{
	public static class HttpUtils
	{
		static public DateTime UnixTimeOrigin = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		static public Int32 ToUnixTime32(this DateTime dateTime)
		{
			return (Int32)Math.Floor((dateTime.ToUniversalTime() - UnixTimeOrigin).TotalSeconds);
		}

		static public DateTime ToDateTime(Int32 unixTime)
		{
			return (UnixTimeOrigin + new TimeSpan(0, 0, unixTime)).ToLocalTime();
		}

		static readonly string downloaderExtension = "gf#";
		static readonly Regex rxDownloadingFileName = new Regex(@"^(?<fname>.+)\.(?<len>[-+a-zA-Z0-9]+)\.(?<mod>[-+a-zA-Z0-9]+)\." + downloaderExtension, RegexOptions.Compiled);
		static readonly Regex rxContentRange = new Regex(@"^bytes\s+(?:\*|(?<first>\d+)-(?<last>\d+))/(?:\*|(?<total>\d+))$", RegexOptions.Compiled);

		static public bool LogInfo { get; set; }

		enum DownloadState { NotStarted, Partial, Completed };

		static string MakePartialFileName(string fname, long len, DateTime lastMod)
		{
			var bytes = BitConverter.GetBytes(len);
			int k = bytes.Reverse().SkipWhile(b => b == 0).Count();
			string slen = Convert.ToBase64String(bytes, 0, k).TrimEnd('=').Replace('/', '-');

			bytes = BitConverter.GetBytes(lastMod.ToUnixTime32());
			k = bytes.Reverse().SkipWhile(b => b == 0).Count();
			string smod = Convert.ToBase64String(bytes, 0, k).TrimEnd('=').Replace('/', '-');

			return String.Join(".", fname, slen, smod, downloaderExtension);
		}

		// not used
		static void TryParsePartialFileName(string fname, out long len, out DateTime lastMod)
		{
			len = -1;
			lastMod = DateTime.MinValue;
			var m = rxDownloadingFileName.Match(fname);
			if (!m.Success)
				throw new FormatException(String.Format("Patial Filename {0} doesn't meet pattern!", fname));
			string slen = m.Groups["len"].Value;
			string smod = m.Groups["mod"].Value;

			var bytes = new byte[8];
			Convert.FromBase64String(slen.Replace('-', '/').PadRight(4 * ((slen.Length + 3) / 4), '=')).CopyTo(bytes, 0);
			len = BitConverter.ToInt64(bytes, 0);

			Array.Clear(bytes, 0, bytes.Length);
			Convert.FromBase64String(smod.Replace('-', '/').PadRight(4 * ((smod.Length + 3) / 4), '=')).CopyTo(bytes, 0);
			int nmod = BitConverter.ToInt32(bytes, 0);
			lastMod = ToDateTime(nmod);
		}

		// not used
		static DownloadState FindDownloadingFileName(string fname, out string dfname)
		{
			if (File.Exists(fname)) // а если есть частично закачанные?
			{
				dfname = fname;
				return DownloadState.Completed;
			}
			var pfiles = Directory.GetFiles(".", String.Join(".", fname, "*", "*", downloaderExtension));
			if (pfiles.Length == 0)
			{
				dfname = null;
				return DownloadState.NotStarted;
			}
			dfname = pfiles[0]; // а если их больше?
			return DownloadState.Partial;
		}

		static public bool CheckFile(string path, out long length, out DateTime lastMod)
		{
			length = -1;
			lastMod = DateTime.MinValue;
			var fi = new FileInfo(path);
			if (!fi.Exists)
				return false;
			length = fi.Length;
			lastMod = fi.LastWriteTime;
			return true;
		}

		/// <summary>
		/// Processes catched WebException
		/// </summary>
		/// <param name="e">WebException</param>
		/// <returns>ShouldTryAgain</returns>
		static bool ProcessWebException(WebException e)
		{
			Debug.Assert(e.Status != WebExceptionStatus.Success);
			Log.WriteLine(e.Message);
			if (e.Status == WebExceptionStatus.ProtocolError)
			{
				var r = (HttpWebResponse)e.Response;
				Debug.Assert(r.StatusCode != HttpStatusCode.OK); // also Partial, etc.
				Log.WriteLine("HTTP Status {0} [{1}] ({2})", (int)r.StatusCode, r.StatusCode, r.StatusDescription);
				switch (r.StatusCode)
				{
					case HttpStatusCode.GatewayTimeout:
					case HttpStatusCode.InternalServerError:
					case HttpStatusCode.RequestTimeout:
					case HttpStatusCode.ServiceUnavailable:
						return true;
					default:
						if (LogInfo)
							Log.WriteLine("Info: Futher attempts are discouraged.");
						return false;
				}
			}
			return true;
		}

		/// <summary>
		/// Check if the resource pointed by the url is available
		/// </summary>
		/// <param name="url">the url of resource</param>
		/// <param name="canResume">server accepts resume downloading</param>
		/// <param name="length">length of resource</param>
		/// <param name="lastMod">last modification date of resource</param>
		/// <returns>
		///		true  if resource is available (no exception occurs);
		///		null  if resource is temporary unavailable, and we should try again later (exception occurs);
		///		false if resource is unavailable (exception occurs).
		/// </returns>
		static public bool? CheckUrl(string url, out bool canResume, out long length, out DateTime lastMod)
		{
			canResume = false;
			length = -1;
			lastMod = DateTime.MinValue;
			var q = (HttpWebRequest)WebRequest.Create(url);
			q.Method = "HEAD";
			try
			{
				using (var r = (HttpWebResponse)q.GetResponse())
				{
					string s = r.Headers[HttpResponseHeader.AcceptRanges];
					if (String.IsNullOrEmpty(s) && LogInfo)
						Log.WriteLine("Warning: 'Accept-Ranges' header is absent, assume 'bytes'.");
					canResume = s != "none"; // == "bytes";
					length = r.ContentLength;
					if (DateTime.TryParse(r.Headers[HttpResponseHeader.LastModified], out lastMod)
						&& lastMod.Kind != DateTimeKind.Local)
						lastMod = lastMod.ToLocalTime();
				}
			}
			catch (WebException e)
			{
				return ProcessWebException(e) ? null : (bool?)false;
			}
			catch (Exception ex)
			{
				Log.WriteLine(ex);
				return false;
			}
			if (length < 0 || lastMod == DateTime.MinValue)
			{
				canResume = false;
				if (LogInfo)
					Log.WriteLine("Warning: Resuming is disabled because of absence of the 'Content-Length' or 'Last-Modified' headers.");
			}
			return true;
		}

		static public string GetFileNameFromUrl(string url)
		{
			string fname = (new Uri(url)).Segments.Last(s => !String.IsNullOrEmpty(s));
			char[] badChars = Path.GetInvalidFileNameChars();
			if (badChars.Any(c => fname.Contains(c)))
			{
				if (LogInfo)
					Log.WriteLine("Output filename \"{0}\" has invalid chars, they will be replaced by underscore (_).", fname);
				fname = new String(fname.Select(c => badChars.Any(bc => bc == c) ? '_' : c).ToArray());
			}
			return fname;
		}

		/// <summary>
		/// Downloads a file from HTTP[S] resource
		/// </summary>
		/// <param name="url">URL of the resource</param>
		/// <param name="path">Path to save the file</param>
		/// <returns>
		///		true  if resource is downloaded and saved;
		///		null  if resource is temporary unavailable, and we should try again later (exception occurs);
		///		false if resource is unavailable (exception occurs).
		/// </returns>
		static public bool? DownloadFile(string url, string path)
		{
			if (LogInfo)
				Log.WriteLine("Downloading: {1} <= {0}", url, path);

			bool canResume;
			long remoteLength;
			DateTime remoteLastMod;

			bool? rslt = CheckUrl(url, out canResume, out remoteLength, out remoteLastMod);
			if (rslt != true)
			{
				if (LogInfo)
					Log.WriteLine("Failure to get HEAD of url.");
				return rslt;
			}

			long localLength;
			DateTime localLastMod;

			if (CheckFile(path, out localLength, out localLastMod))
			{
				if (localLength == remoteLength && localLastMod == remoteLastMod)
				{
					if (LogInfo)
						Log.WriteLine("File '{0}' already exists and has not changed.", path);
					return true;
				}
				Log.WriteLine("File already exists, but remote file has changed or not enough identity info.");
				if (LogInfo)
				{
					if (remoteLength >= 0)
						Log.WriteLine("Local length = {0}, remote length = {1}.", localLength, remoteLength);
					else
						Log.WriteLine("The remote length header 'Content-Length' is absent.");
					if (remoteLastMod != DateTime.MinValue)
						Log.WriteLine("Local date = {0}, remote date = {1}.", localLastMod, remoteLastMod);
					else
						Log.WriteLine("The remote date header 'Last-Modified' is absent.");
				}
				Log.WriteLine("You should delete this local file manually.");
				return false;
			}

			string partName;
			long written = 0;
			if (canResume)
			{
				partName = MakePartialFileName(path, remoteLength, remoteLastMod);
				if (File.Exists(partName))
					written = (new FileInfo(partName)).Length;
			}
			else
			{
				partName = path + "." + downloaderExtension;
				if (LogInfo)
					Log.WriteLine("Resuming download is not supported");
				if (File.Exists(partName))
					File.Delete(partName);
			}

			if (written > 0)
				Log.WriteLine("Resuming download from {0} MB ({1} bytes) ...", written >> 20, written);
			else
				Log.WriteLine("Starting download ...");

			var q = (HttpWebRequest)WebRequest.Create(url);
			if (written > 0)
				q.AddRange(written);
			try
			{
				using (var r = (HttpWebResponse)q.GetResponse())
				using (var rstream = r.GetResponseStream())
				using (var fs = new FileStream(partName, FileMode.Append, FileAccess.Write, FileShare.Read))
				{
					bool acceptRanges = r.Headers[HttpResponseHeader.AcceptRanges] != "none"; // == "bytes";
					if (!acceptRanges && canResume)
					{
						Log.WriteLine("Warning: server accepted ranges in HEAD and is not in GET");
						//return null;
					}
					if (written > 0)
					{
						if (r.StatusCode != HttpStatusCode.PartialContent)
						{
							Log.WriteLine("Error: server accepted resume, but returned {0} ({1}), must return 206 (Partial Content).",
								(int)r.StatusCode, r.StatusDescription);
							return null;
						}
						string s = r.Headers[HttpResponseHeader.ContentRange];
						var m = rxContentRange.Match(s ?? "");
						long first, last, total;
						if (!m.Success ||
							!m.Groups["first"].Success || !m.Groups["last"].Success || !m.Groups["total"].Success ||
							!long.TryParse(m.Groups["first"].Value, out first) ||
							!long.TryParse(m.Groups["last"].Value, out last) ||
							!long.TryParse(m.Groups["total"].Value, out total) ||
							first != written || last + 1 != total || total != remoteLength)
						{
							Log.WriteLine("Error: 'Content-Range' has bad format or bad values: '{0}'", s);
							return null;
						}
					}
					long rlen = r.ContentLength;
					if (remoteLength >= 0 && (rlen < 0 || rlen + written != remoteLength))
					{
						Log.WriteLine("Error: remote file length changed: was {0}, now it is {1}",
							remoteLength, rlen < 0 ? "undefined" : (rlen + written) + " bytes");
						return null;
					}
					DateTime rmod;
					if (DateTime.TryParse(r.Headers[HttpResponseHeader.LastModified], out rmod)
						&& rmod.Kind != DateTimeKind.Local)
						rmod = rmod.ToLocalTime();
					if (remoteLastMod != DateTime.MinValue && rmod != remoteLastMod)
					{
						Log.WriteLine("Error: remote file date changed: was {0}, now it is {1} bytes", remoteLastMod, rmod);
						return null;
					}

					if (remoteLength >= 0)
						Log.WriteLine("Left to get: {0} MB ({1} bytes, {2})", rlen >> 20, rlen,
							((double)rlen / remoteLength).ToString("P2", CultureInfo.InvariantCulture));
					byte[] buf = new byte[8 << 10];
					int nr;
					while ((nr = rstream.Read(buf, 0, buf.Length)) > 0)
					{
						fs.Write(buf, 0, nr);
						written += nr;
					}
				}
			}
			catch (WebException e)
			{
				if (LogInfo)
					Log.WriteLine("Error, dowloading failure. Saved {0} bytes of {1}, the rest is {2}", written, remoteLength, remoteLength - written);
				return ProcessWebException(e) ? null : (bool?)false;
			}
			catch (Exception ex)
			{
				Log.WriteLine(ex);
				return false;
			}

			if (remoteLastMod != DateTime.MinValue)
				(new FileInfo(partName)).LastWriteTimeUtc = remoteLastMod;

			File.Move(partName, path);

			if (remoteLength >= 0 && LogInfo)
			{
				if (written == remoteLength)
					Log.WriteLine("Done, saved {0} MB ({1} bytes)", written >> 20, written);
				else
					Log.WriteLine("Warning, saved only {0} bytes of {1}, the rest is {2}", written, remoteLength, remoteLength - written);
			}

			return true;
		}
	}
}
