using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace FairyGUI
{
	/// <summary>
	/// Creates UnityEngine.Font objects from ttf/otf files that live inside a FairyGUI project directory.
	/// The render server is a built player, so neither AssetDatabase nor project Resources can reach those
	/// files. Instead the font file is registered for the current session via AddFontResourceEx, then Unity's
	/// OS-font dynamic font API resolves it by its real family name (parsed from the file's 'name' table).
	/// Registered files are removed again when the application quits.
	/// </summary>
	static public class ProjectTtfFontLoader
	{
		static Dictionary<string, Font> sCache = new Dictionary<string, Font>(StringComparer.OrdinalIgnoreCase);
		static HashSet<string> sRegisteredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		static bool sQuitHookRegistered;

		/// <summary>
		/// Returns a UnityEngine.Font backed by the ttf/otf file at absPath, or null if it cannot be resolved.
		/// Results (including failures) are cached per path so per-request package re-parsing stays cheap.
		/// </summary>
		static public Font GetOrAddFont(string absPath)
		{
			Font cached;
			if (sCache.TryGetValue(absPath, out cached))
				return cached;

			Font font = CreateFont(absPath);
			sCache[absPath] = font;
			return font;
		}

		static Font CreateFont(string absPath)
		{
			List<string> familyNames;
			try
			{
				familyNames = ParseFamilyNames(absPath);
			}
			catch (Exception e)
			{
				Debug.LogWarning("ProjectTtfFontLoader: could not read font file " + absPath + " (" + e.Message + ")");
				return null;
			}

			if (familyNames.Count == 0)
			{
				Debug.LogWarning("ProjectTtfFontLoader: no family name found in font file " + absPath);
				return null;
			}

			RegisterWithOS(absPath);

			HashSet<string> installed = GetInstalledFamilyNames();

			List<string> candidates = new List<string>();
			foreach (string familyName in familyNames)
			{
				if (installed.Contains(familyName))
					candidates.Add(familyName);
			}

			if (candidates.Count == 0)
			{
				//Not visible to enumeration yet: keep the previous behaviour (fall back to the default font)
				//and surface a neutral hint for the developer.
				Debug.LogWarning("ProjectTtfFontLoader: font family of " + absPath + " (" + string.Join(" / ", familyNames.ToArray())
					+ ") is not available through OS fonts yet, rendering will use the default font.");
				return null;
			}

			return Font.CreateDynamicFontFromOSFont(candidates.ToArray(), 16);
		}

		static HashSet<string> GetInstalledFamilyNames()
		{
			HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			string[] names = Font.GetOSInstalledFontNames();
			if (names != null)
			{
				for (int i = 0; i < names.Length; i++)
					result.Add(CleanFamilyName(names[i]));
			}
			return result;
		}

		//Unity may report families as "Name (TrueType)"; the OS-font creation API takes plain family names.
		static string CleanFamilyName(string name)
		{
			if (string.IsNullOrEmpty(name))
				return name;

			int pos = name.IndexOf('(');
			if (pos > 0)
				return name.Substring(0, pos).TrimEnd();

			return name;
		}

		static void RegisterWithOS(string absPath)
		{
			if (sRegisteredFiles.Contains(absPath))
				return;

			//Flag 0 (not FRHDWNT_PRIVATE): the font must be enumerable by GDI/DirectWrite for
			//CreateDynamicFontFromOSFont to see it. It stays registered for the session only.
			int added = AddFontResourceExW(absPath, 0, IntPtr.Zero);
			if (added > 0)
			{
				sRegisteredFiles.Add(absPath);
				EnsureQuitHook();
			}
			//added == 0 is not fatal: the font may already be installed system-wide.

			IntPtr result;
			SendMessageTimeoutW(HWND_BROADCAST, WM_FONTCHANGE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out result);
		}

		static void EnsureQuitHook()
		{
			if (sQuitHookRegistered)
				return;

			sQuitHookRegistered = true;
			Application.quitting += OnQuitting;
		}

		static void OnQuitting()
		{
			foreach (string path in sRegisteredFiles)
				RemoveFontResourceExW(path, 0, IntPtr.Zero);
			sRegisteredFiles.Clear();

			IntPtr result;
			SendMessageTimeoutW(HWND_BROADCAST, WM_FONTCHANGE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out result);
		}

		#region ttf/otf 'name' table parsing

		const uint TTCF_TAG = 0x74746366; //'ttcf'

		/// <summary>
		/// Returns font family name candidates (name records 1 and 16, Windows/Unicode platforms) in
		/// preference order. Per-weight Source Han files carry the weight inside family name 1, while the
		/// source family (16) may be shared, so record 1 is tried first.
		/// </summary>
		static public List<string> ParseFamilyNames(string absPath)
		{
			List<string> result = new List<string>();

			using (FileStream fs = new FileStream(absPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
			using (BinaryReader br = new BinaryReader(fs))
			{
				long fontOffset = 0;
				fs.Position = 0;
				if (ReadUInt32BE(br) == TTCF_TAG)
				{
					//Font collection: use its first font.
					fs.Position = 8;
					uint numFonts = ReadUInt32BE(br);
					if (numFonts == 0)
						return result;
					fontOffset = ReadUInt32BE(br);
				}

				fs.Position = fontOffset;
				ReadUInt32BE(br); //sfnt version
				int numTables = ReadUInt16BE(br);
				fs.Position += 6; //searchRange/entrySelector/rangeShift

				long nameTableOffset = 0;
				for (int i = 0; i < numTables; i++)
				{
					byte[] tag = br.ReadBytes(4);
					bool isName = tag[0] == (byte)'n' && tag[1] == (byte)'a' && tag[2] == (byte)'m' && tag[3] == (byte)'e';
					fs.Position += 4; //checkSum
					uint offset = ReadUInt32BE(br);
					fs.Position += 4; //length
					if (isName)
					{
						nameTableOffset = offset;
						break;
					}
				}

				if (nameTableOffset == 0)
					return result;

				fs.Position = nameTableOffset;
				fs.Position += 2; //format
				int count = ReadUInt16BE(br);
				int storageOffset = ReadUInt16BE(br);

				List<string> record1 = new List<string>();
				List<string> record16 = new List<string>();
				for (int i = 0; i < count; i++)
				{
					fs.Position = nameTableOffset + 6 + i * 12;
					int platformID = ReadUInt16BE(br);
					fs.Position += 4; //encodingID + languageID
					int nameID = ReadUInt16BE(br);
					int length = ReadUInt16BE(br);
					int stringOffset = ReadUInt16BE(br);

					if (nameID != 1 && nameID != 16)
						continue;
					//platform 1 (Mac) strings are legacy encodings, English/Unicode (3/0) are enough here
					if (platformID != 3 && platformID != 0)
						continue;

					long pos = fs.Position;
					fs.Position = nameTableOffset + storageOffset + stringOffset;
					byte[] raw = br.ReadBytes(length);
					fs.Position = pos;

					string value = Encoding.BigEndianUnicode.GetString(raw);
					if (string.IsNullOrEmpty(value))
						continue;

					List<string> target = nameID == 1 ? record1 : record16;
					if (!target.Contains(value))
						target.Add(value);
				}

				result.AddRange(record1);
				result.AddRange(record16);
			}

			return result;
		}

		static uint ReadUInt32BE(BinaryReader br)
		{
			byte[] b = br.ReadBytes(4);
			if (b.Length < 4)
				throw new EndOfStreamException();
			return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
		}

		static int ReadUInt16BE(BinaryReader br)
		{
			byte[] b = br.ReadBytes(2);
			if (b.Length < 2)
				throw new EndOfStreamException();
			return (b[0] << 8) | b[1];
		}

		#endregion

		#region win32

		const uint WM_FONTCHANGE = 0x001D;
		const uint SMTO_ABORTIFHUNG = 0x0002;
		static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);

		[DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern int AddFontResourceExW(string lpszFilename, uint fl, IntPtr pdv);

		[DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern bool RemoveFontResourceExW(string lpszFilename, uint fl, IntPtr pdv);

		[DllImport("user32.dll", SetLastError = true)]
		static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr lpResult);

		#endregion
	}
}
