﻿﻿//
// DotNetCoreVersion.cs
//
// Author:
//       Matt Ward <matt.ward@xamarin.com>
//
// Copyright (c) 2017 Xamarin Inc. (http://xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using MonoDevelop.Core;

namespace MonoDevelop.DotNetCore
{
	class DotNetCoreVersion : IEquatable<DotNetCoreVersion>, IComparable, IComparable<DotNetCoreVersion>
	{
		internal static readonly DotNetCoreVersion MinimumSupportedSdkVersion = new DotNetCoreVersion (2, 1, 602);
		internal static readonly DotNetCoreVersion MinimumSupportedSdkVersion22 = new DotNetCoreVersion (2, 2, 202);
		internal static readonly DotNetCoreVersion MinimumSupportedSdkVersion30 = new DotNetCoreVersion (3, 0, 100) {
			ReleaseLabel = "preview3-010431",
			IsPrerelease = true
		};

		internal DotNetCoreVersion (int major, int minor, int patch)
			: this (new Version (major, minor, patch))
		{
		}

		DotNetCoreVersion (Version version)
		{
			Version = version;
		}

		public Version Version { get; private set; }

		public int Major => Version.Major;

		public int Minor => Version.Minor;

		public int Patch => Version.Build;

		// Used by UpdaterService to generate VersionId
		public int Revision { get; private set; }

		public string OriginalString { get; private set; }

		public bool IsPrerelease { get; private set; }

		public string ReleaseLabel { get; private set; }

		public override string ToString ()
		{
			if (!string.IsNullOrEmpty (OriginalString))
				return OriginalString;

			return Version.ToString ();
		}

		/// <summary>
		/// Stable runtime version: 1.0.3
		/// Pre-release runtime version: 2.0.0-preview2-002093-00
		/// </summary>
		public static DotNetCoreVersion Parse (string input)
		{
			if (string.IsNullOrEmpty (input))
				throw new ArgumentException (".NET Core version cannot be null or an empty string.", nameof (input));

			if (TryParse (input, out var version))
				return version;

			throw new FormatException (string.Format ("Invalid .NET Core version: '{0}'", input));
		}

		public static bool TryParse (string input, out DotNetCoreVersion result)
		{
			result = null;

			if (string.IsNullOrEmpty (input))
				return false;

			string versionString = input;
			string releaseLabel = string.Empty;
			int revision = 0;
			int prereleaseLabelStart = input.IndexOf ('-');
			if (prereleaseLabelStart >= 0) {
				versionString = input.Substring (0, prereleaseLabelStart);
				releaseLabel = input.Substring (prereleaseLabelStart + 1);
				int revisionLabelStart = input.IndexOf ('-', prereleaseLabelStart + 1) + 1;
				if (revisionLabelStart > 0) {
					int revisionLabelEnd = input.IndexOf ('-', revisionLabelStart);
					string revisionString;
					if (revisionLabelEnd < 0) {
						revisionString = input.Substring (revisionLabelStart);
					} else {
						revisionString = input.Substring (revisionLabelStart, revisionLabelEnd - revisionLabelStart);
					}
					if (!int.TryParse (revisionString , out revision)) {
						throw new FormatException (string.Format ("Invalid .NET Core version: '{0}'", input));
					}
				}
			}

			if (!Version.TryParse (versionString, out var version))
				return false;

			result = new DotNetCoreVersion (version) {
				OriginalString = input,
				IsPrerelease = prereleaseLabelStart >= 0,
				Revision = revision,
				ReleaseLabel = releaseLabel
			};

			return true;
		}

		public override int GetHashCode ()
		{
			return OriginalString.GetHashCode ();
		}

		public override bool Equals (object obj)
		{
			return Equals (obj as DotNetCoreVersion);
		}

		public bool Equals (DotNetCoreVersion other)
		{
			return CompareTo (other) == 0;
		}

		public int CompareTo (DotNetCoreVersion other)
		{
			return Compare (this, other);
		}

		public int CompareTo (object obj)
		{
			return CompareTo (obj as DotNetCoreVersion);
		}

		public static int Compare (DotNetCoreVersion x, DotNetCoreVersion y)
		{
			if (ReferenceEquals (x, y))
				return 0;

			if (ReferenceEquals (y, null))
				return 1;

			if (ReferenceEquals (x, null))
				return -1;

			int result = x.Version.CompareTo (y.Version);
			if (result != 0)
				return result;

			if (!x.IsPrerelease && !y.IsPrerelease)
				return result;

			// Pre-release versions are lower than stable versions.
			if (x.IsPrerelease && !y.IsPrerelease)
				return -1;

			if (!x.IsPrerelease && y.IsPrerelease)
				return 1;

			return StringComparer.OrdinalIgnoreCase.Compare (x.ReleaseLabel, y.ReleaseLabel);
		}

		public static bool operator ==(DotNetCoreVersion x, DotNetCoreVersion y)
		{
			return Compare (x, y) == 0;
		}

		public static bool operator !=(DotNetCoreVersion x, DotNetCoreVersion y)
		{
			return Compare (x, y) != 0;
		}

		public static bool operator < (DotNetCoreVersion x, DotNetCoreVersion y)
		{
			return Compare (x, y) < 0;
		}

		public static bool operator <= (DotNetCoreVersion x, DotNetCoreVersion y)
		{
			return Compare (x, y) <= 0;
		}

		public static bool operator > (DotNetCoreVersion x, DotNetCoreVersion y)
		{
			return Compare (x, y) > 0;
		}

		public static bool operator >= (DotNetCoreVersion x, DotNetCoreVersion y)
		{
			return Compare (x, y) >= 0;
		}

		public static DotNetCoreVersion GetDotNetCoreVersionFromDirectory (string directory)
		{
			string directoryName = Path.GetFileName (directory);
			if (TryParse (directoryName, out var version))
				return version;

			LoggingService.LogInfo ("Unable to parse version from directory. '{0}'", directory);
			return null;
		}

		// from 8.1 on, we are only supporting .NET Core SDK based on Nuget 5.0
		// Minimum SDKs:
		//			- 2.1.6XX
		//			- 2.2.2XX
		//			- 3.0 Preview 3
		public static bool IsSdkSupported (DotNetCoreVersion version)
		{
			if (version.Major == 2) {
				if (version.Minor == 1)
					return version >= MinimumSupportedSdkVersion;

				return version >= MinimumSupportedSdkVersion22;
			}

			if (version.Major == 3) {
				return version >= MinimumSupportedSdkVersion30;
			}

			return false;
		}
	}
}
