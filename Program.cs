using System;
using System.Collections.Generic;
using Microsoft.VisualBasic.FileIO;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Configuration;
using Ude;

namespace CrLfTool
{
	class Program
	{
		private static Regex UnixLineEndingRx = new Regex("([^\r])\n", RegexOptions.Compiled);
		private static Regex WindowsLineEndingRx = new Regex("\r\n", RegexOptions.Compiled);

		static DelOpt delOpt = DelOpt.RecycleBin;

		[STAThread]
		static void Main(string[] args)
		{
			var indexer = new Indexer();
			indexer.Init();
			MainAsync(args, indexer).Wait();
			indexer.Save();
		}

		private async static Task MainAsync(string[] args, Indexer index)
		{
			var result = true;
			string target;

			if ((args.Length < 3) || (args[0] != "fix" && args[0] != "validate") || (args[1] != "unix" && args[1] != "windows"))
			{
				Console.WriteLine("Usage: Crlf fix|validate unix|windows [-f] path");
				return;
			}
			if (args[2].ToUpper() == "-f")
			{
				target = args[3];
				delOpt = DelOpt.Forever;
			}
			else
			{
				target = args[2];
			}

			int targetType;

			if (File.Exists(target)) targetType = 2;
			else if (Directory.Exists(target)) targetType = 1;
			else targetType = 0;

			if (targetType == 0)
			{
				Console.WriteLine("Path should be valid file or directory");
				return;
			}

			ActionType at = args[0] == "fix" ? ActionType.Fix : ActionType.Validate;
			LineEnding le = args[1] == "unix" ? LineEnding.Unix : LineEnding.Windows;

			if (targetType == 1)
			{
				if (!await ProcessDirectory(new DirectoryInfo(target), at, le, index))
				{
					result = false;
				}
			}
			else if (targetType == 2)
			{
				if (!await ProcessFile(new FileInfo(target), at, le, index))
				{
					result = false;
				}
			}

			if (!result)
			{
				Environment.Exit(-1);
			}
		}
		private static async Task<bool> ProcessDirectory(DirectoryInfo dirInfo, ActionType actionType, LineEnding lineEnding, Indexer index)
		{
			bool result = true;
			if (!dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) && !dirInfo.Name.IsValidFolder())
			{
				foreach (var d in dirInfo.GetDirectories())
				{
					if (!await ProcessDirectory(d, actionType, lineEnding, index))
					{
						result = false;
					}
				}
				foreach (var f in dirInfo.GetFiles())
				{
					if (!await ProcessFile(f, actionType, lineEnding, index))
					{
						result = false;
					}
				}
			}
			return result;
		}

		private static async Task<bool> ProcessFile(FileInfo fileInfo, ActionType actionType, LineEnding lineEnding, Indexer index)
		{
			if (!fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) && fileInfo.Extension.IsValidExtensionForProcessing())
			{
				var item = index.Get(fileInfo.FullName);
				if (item != null && item.Item1 == fileInfo.LastWriteTimeUtc && item.Item2 == true) return true;
				if (actionType == ActionType.Fix)
				{
					return await FixFile(fileInfo, lineEnding, index);
				}
				else if (actionType == ActionType.Validate)
				{
					return await ValidateFile(fileInfo, lineEnding, index);
				}
			}
			return true;
		}

		private static async Task<bool> FixFile(FileInfo fileInfo, LineEnding lineEnding, Indexer index)
		{
			string content;
			Encoding enc = Encoding.Default;

			using (FileStream fs = File.OpenRead(fileInfo.FullName))
			{
				Ude.CharsetDetector cdet = new CharsetDetector();
				cdet.Feed(fs);
				cdet.DataEnd();
				if (cdet.Charset != null && cdet.Confidence >= 0.5)
				{
					enc = Encoding.GetEncoding(cdet.Charset);
				}
				fs.Seek(0, SeekOrigin.Begin);

				using (var rdr = new StreamReader(fs, enc))
				{
					content = await rdr.ReadToEndAsync();
				}
				fs.Close();
			}

			if (lineEnding == LineEnding.Unix)
			{
				content = WindowsLineEndingRx.Replace(content, "\n");
			}
			if (lineEnding == LineEnding.Windows)
			{
				content = UnixLineEndingRx.Replace(content, "$1\r\n");
			}

			if (delOpt == DelOpt.Forever)
				File.Delete(fileInfo.FullName);
			else
				FileSystem.DeleteFile(fileInfo.FullName, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

			using (var wrt = new StreamWriter(fileInfo.FullName, false, Encoding.UTF8))
			{
				await wrt.WriteAsync(content);
				wrt.Close();
			}

			index.Upsert(fileInfo.FullName, new Tuple<DateTime, bool>(new FileInfo(fileInfo.FullName).LastWriteTimeUtc, true));
			return true;
		}

		private static async Task<bool> ValidateFile(FileInfo fileInfo, LineEnding lineEnding, Indexer index)
		{
			using (var rdr = File.OpenText(fileInfo.FullName))
			{
				var content = await rdr.ReadToEndAsync();
				rdr.Close();
				if (lineEnding == LineEnding.Unix)
				{
					if (WindowsLineEndingRx.Match(content).Success)
					{
						Console.WriteLine($"Invalid line ending in file: {fileInfo.FullName}");
						index.Upsert(fileInfo.FullName, new Tuple<DateTime, bool>(fileInfo.LastWriteTimeUtc, false));
						return false;
					}
				}
				else if (lineEnding == LineEnding.Windows)
				{
					if (UnixLineEndingRx.Match(content).Success)
					{
						Console.WriteLine($"Invalid line ending in file: {fileInfo.FullName}");
						index.Upsert(fileInfo.FullName, new Tuple<DateTime, bool>(fileInfo.LastWriteTimeUtc, false));
						return false;
					}
				}
			}
			index.Upsert(fileInfo.FullName, new Tuple<DateTime, bool>(fileInfo.LastWriteTimeUtc, true));
			return true;
		}
	}

	internal class Indexer
	{
		private const string IndexFileName = "index.bin";

		private Dictionary<string, Tuple<DateTime, bool>> _index;

		public Tuple<DateTime, bool> Get(string path)
		{
			if (_index == null) new InvalidOperationException("You should init indexer before usage. Call Init method");
			if (_index.ContainsKey(path))
			{
				return _index[path];
			}
			else
			{
				return null;
			}
		}

		public void Upsert(string path, Tuple<DateTime, bool> value)
		{
			if (_index == null) new InvalidOperationException("You should init indexer before usage. Call Init method");
			_index[path] = value;
		}

		public void Init()
		{
			if (File.Exists(IndexFileName))
			{
				using (var fs = File.OpenRead(IndexFileName))
				{
					var f = new BinaryFormatter();
					_index = (Dictionary<string, Tuple<DateTime, bool>>)f.Deserialize(fs);
					fs.Close();
				}
			}
			else
			{
				_index = new Dictionary<string, Tuple<DateTime, bool>>();
			}
		}
		public void Save()
		{
			if (File.Exists(IndexFileName))
			{
				File.Delete(IndexFileName);
			}
			using (var fs = File.OpenWrite(IndexFileName))
			{
				var f = new BinaryFormatter();
				f.Serialize(fs, _index);
				fs.Close();
			}
		}

	}

	internal enum ActionType
	{
		Fix,
		Validate
	}

	internal enum LineEnding
	{
		Unix,
		Windows
	}

	internal enum DelOpt
	{
		Forever,
		RecycleBin
	}

	internal static class StringExtensions
	{
		private static List<string> _extensionList = string.IsNullOrEmpty(ConfigurationManager.AppSettings["ExtensionList"])
			? new List<string> { ".cs", ".cshtml", ".txt", ".js", ".xml", ".css", ".less", ".scss", ".md" }
			: new List<string>(ConfigurationManager.AppSettings["ExtensionList"].Split(';'));
		private static List<string> _excludeFolderList = string.IsNullOrEmpty(ConfigurationManager.AppSettings["ExcludeFolderList"])
			? new List<string> { ".git", "bin" }
			: new List<string>(ConfigurationManager.AppSettings["ExcludeFolderList"].Split(';'));
		public static bool IsValidExtensionForProcessing(this string extension)
		{
			return _extensionList.Contains(extension);
		}
		public static bool IsValidFolder(this string extension)
		{
			return _excludeFolderList.Contains(extension);
		}
	}
}
