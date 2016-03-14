using System;
using System.Linq;
using System.IO;
using System.Globalization;
using Tsul.Network;
using Log = System.Console;

namespace Tsul.Tests
{
	class GetFile
	{
		static readonly int maxRetries = 3;

		public static void Main(string[] args)
		{
			if (args.Length == 0)
			{
				Console.WriteLine("Usage: GetFile <url> [filename|directory]");
				return;
			}
			string url = args[0];
			string dir, file;
			if (args.Length > 1)
			{
				if (Directory.Exists(args[1]))
				{
					dir = args[1].TrimEnd('/', '\\');
					file = HttpUtils.GetFileNameFromUrl(url);
				}
				else
				{
					dir = Path.GetDirectoryName(args[1]);
					file = Path.GetFileName(args[1]);
				}
			}
			else
			{
				dir = "";
				file = HttpUtils.GetFileNameFromUrl(url);
			}
			string path = Path.Combine(dir, file);
			Log.WriteLine("ServicePointManager.DefaultConnectionLimit = {0}", System.Net.ServicePointManager.DefaultConnectionLimit);
			HttpUtils.LogInfo = true;
			bool? rslt = null;
			for (int i = 0; i <= maxRetries && (rslt = HttpUtils.DownloadFile(url, path)) == null; i++)
				if (i != maxRetries)
					Log.WriteLine(" --- Retry {0} ---", i + 1);
			switch (rslt)
			{
				case true: Log.WriteLine("Success"); break;
				case false: Log.WriteLine("Error"); break;
				case null: Log.WriteLine("Warning, will try again later"); break;
			}
			//if (rslt == true)
			//{
			//	var descs = DescriptIon.LoadFrom(dir);
			//	descs[file] = new FileDescription { FileName = file, Url = url };
			//	descs.Save();
			//}
		}
	}
}
