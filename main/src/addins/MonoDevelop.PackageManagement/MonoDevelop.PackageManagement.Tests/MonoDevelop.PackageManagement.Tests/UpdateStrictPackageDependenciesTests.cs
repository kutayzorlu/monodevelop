//
// UpdateStrictPackageDependenciesTests.cs
//
// Author:
//       Matt Ward <matt.ward@microsoft.com>
//
// Copyright (c) 2019 Microsoft Corporation
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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MonoDevelop.PackageManagement.Tests.Helpers;
using MonoDevelop.Projects;
using NuGet.Packaging.Core;
using NuGet.Versioning;
using NUnit.Framework;
using UnitTests;

namespace MonoDevelop.PackageManagement.Tests
{
	[TestFixture]
	public class UpdateStrictPackageDependenciesTests : RestoreTestBase
	{
		[Test]
		public async Task UpdatePackage_ReferenceInsideItemGroupWithCondition_ReferenceRemainsInsideItemGroupAfterUpdate ()
		{
			string solutionFileName = Util.GetSampleProject ("StrictNuGetDependency", "StrictNuGetDependency.sln");
			using (solution = (Solution)await Services.ProjectService.ReadWorkspaceItem (Util.GetMonitor (), solutionFileName)) {
				CreateNuGetConfigFile (solution.BaseDirectory);
				var project = (DotNetProject)solution.FindProjectByName ("StrictNuGetDependency");

				await RestoreNuGetPackages (solution);

				// Update NuGet packages
				var packages = new List<PackageIdentity> ();
				packages.Add (new PackageIdentity ("Test.Xam.Strict.Dependency.A", NuGetVersion.Parse ("1.1.0")));
				packages.Add (new PackageIdentity ("Test.Xam.Strict.Dependency.B", NuGetVersion.Parse ("1.1.0")));
				await UpdateNuGetPackages (project, packages);

				string expectedXml = Util.ToSystemEndings (File.ReadAllText (project.FileName.ChangeExtension (".csproj-saved")));
				string actualXml = Util.ToSystemEndings (File.ReadAllText (project.FileName));
				Assert.AreEqual (expectedXml, actualXml);
			}
		}

		Task UpdateNuGetPackages (DotNetProject project, IEnumerable<PackageIdentity> packages)
		{
			var solutionManager = new MonoDevelopSolutionManager (project.ParentSolution);
			var context = CreateNuGetProjectContext (solutionManager.Settings);

			var sources = solutionManager.CreateSourceRepositoryProvider ().GetRepositories ().ToList ();

			var action = new UpdateMultipleNuGetPackagesAction (
				sources,
				solutionManager,
				context);

			action.AddProject (new DotNetProjectProxy (project));

			foreach (var package in packages) {
				action.AddPackageToUpdate (package);
			}

			return Task.Run (() => {
				action.Execute ();
			});
		}

		//Task UpdateNuGetPackagesBroken (DotNetProject project, IEnumerable<PackageIdentity> packages)
		//{
		//	var solutionManager = new MonoDevelopSolutionManager (project.ParentSolution);
		//	var context = CreateNuGetProjectContext (solutionManager.Settings);

		//	var sources = solutionManager.CreateSourceRepositoryProvider ().GetRepositories ().ToList ();

		//	var actions = new List<InstallNuGetPackageAction> ();

		//	foreach (var package in packages) {
		//		var action = new InstallNuGetPackageAction (
		//			sources,
		//			solutionManager,
		//			new DotNetProjectProxy (project),
		//			context) {
		//			PackageId = package.Id,
		//			Version = package.Version
		//		};
		//		actions.Add (action);
		//	}

		//	return Task.Run (() => {
		//		foreach (var action in actions) {
		//			action.Execute ();
		//		}
		//	});
		//}
	}
}
