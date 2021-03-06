﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using EnvDTE80;
using NuGet;

namespace PackageVisualizer
{
    /// <summary>
    /// Some credit can be attributed to Pascal Laurin from: http://pascallaurin42.blogspot.com/2014/06/visualizing-nuget-packages-dependencies.html. 
    /// I modified a lot of his work from his LinqPad query to suit the needs of this extension. He gave me a great starting point.
    /// </summary>
    public class NugetPackageVisualizer
    {
        private readonly DTE2 _vsEnvironment;
        private readonly string _solutionFolder;
        private readonly List<Project> _projectList = new List<Project>();
        private readonly List<NugetPackage> _packageList = new List<NugetPackage>();
        private readonly string[] _projectExtensionExclusions = { ".vdproj", ".ndproj", ".wdproj", ".shfbproj", ".modelproj" };
        private readonly XNamespace _dgmlns = "http://schemas.microsoft.com/vs/2009/dgml";

        public NugetPackageVisualizer(DTE2 vsEnvironment)
        {
            _vsEnvironment = vsEnvironment;
            _solutionFolder = Path.GetDirectoryName(vsEnvironment.Solution.FullName);
        }

        public void GenerateDgmlFile(string filename)
        {
            LoadProjects();
            LoadPackageConfigs();

            var graph = new XElement(
                _dgmlns + "DirectedGraph", new XAttribute("GraphDirection", "LeftToRight"),
                CreateNodes(),
                CreateLinks(),
                CreateStyles());

            var doc = new XDocument(graph);
            doc.Save(filename);
        }

        #region DGML Elements
        private XElement CreateStyles()
        {
            return new XElement(_dgmlns + "Styles",
                CreateStyle("Project", "Blue"),
                CreateStyle("Package", "White"));
        }

        private XElement CreateNodes()
        {
            return new XElement(_dgmlns + "Nodes", _projectList.Select(p => CreateNode(p.Name, "Project")),
                _packageList.Select(p => CreateNode(p.Name + " " + p.Version, "Package")));
        }

        private XElement CreateLinks()
        {
            var linkElements = new List<XElement>();
            var allPackages = _projectList.SelectMany(p => p.Packages.Select(pa => new ProjectNugetPackage { Project = p, Package = pa }));

            #region Add Package Dependency Links

            /*for each nuget package referenced under a project, get all of its dependencies and create a package dependency link for each
            example:
            <Link Source="Microsoft.AspNet.WebPages 3.2.3" Target="Microsoft.AspNet.Razor 3.2.3" Category="Package Dependency" />
            <Link Source="Microsoft.AspNet.WebPages 3.2.3" Target="Microsoft.Web.Infrastructure 1.0.0.0" Category="Package Dependency" />
            */
            foreach (var package in allPackages)
            {
                var packageDependencies = GetPackageDependencies(package.Package.Name, package.Package.Version);
                foreach (var packageDependency in packageDependencies)
                {
                    linkElements.Add(CreateLink(
                        package.Package.Name + " " + package.Package.Version,
                        packageDependency.Name + " " + packageDependency.Version,
                        "Package Dependency"));
                }
            }

            #endregion

            #region Add Installed Package Links

            /*for each nuget package installed under a project, create a installed package link for it
            example:
            <Link Source="ThisIsAnExample.Project.Name" Target="AutoFixture.AutoMoq 3.30.4" Category="Installed Package" />
            */
            const string installedPackageCategory = "Installed Package";
            foreach (var installedPackage in allPackages)
            {
                var packageId = installedPackage.Package.Name + " " + installedPackage.Package.Version;
                var link = CreateLink(installedPackage.Project.Name, packageId, installedPackageCategory);
                linkElements.Add(link);
            }

            #endregion

            #region Remove Installed Package Links where dependencies are not directly under project

            /*now we need to iterate through all the installed package links, to remove links that are not directly referenced under the project
            example:

            ThisIsAnExample.Project.Name 
                has a package dependency on Microsoft.AspNet.Web.Optimization 1.1.3
                    which has a package dependency on WebGrease 1.6.0
                        which has a package dependency on Antlr 3.4.1.9004
                        
            So in this example, ThisIsAnExample.Project.Name only has a link directly to Microsoft.AspNet.Web.Optimization 1.1.3, 
            and not to WebGrease 1.6.0 or Antlr 3.4.1.9004, because those are part of Microsoft.AspNet.Web.Optimization's dependencies
            */

            var packageLinksToAdd = linkElements.Where(e => e.Attribute("Category").Value.Equals(installedPackageCategory)).ToList();

            var elementsToRemove = new List<XElement>();
            foreach (var link in packageLinksToAdd)
            {
                //remove any links that are not directly under the project (see comment above)
                if (!ProjectLinkIsDirectDependency(link, allPackages))
                {
                    elementsToRemove.Add(link);
                }
            }

            foreach (var elementToRemove in elementsToRemove)
            {
                linkElements.Remove(elementToRemove);
            }

            #endregion

            return new XElement(_dgmlns + "Links", linkElements);
        }

        /* Given a project link, iterate through all packages for that project, to determine if the link is direct, or is part of another dependency.
        example:

        ThisIsAnExample.Project.Name 
                has a package dependency on Microsoft.AspNet.Web.Optimization 1.1.3
                    which has a package dependency on WebGrease 1.6.0
                        which has a package dependency on Antlr 3.4.1.9004
                        
            So in this example, ThisIsAnExample.Project.Name only has a link directly to Microsoft.AspNet.Web.Optimization 1.1.3, 
            and not to WebGrease 1.6.0 or Antlr 3.4.1.9004, because those are part of Microsoft.AspNet.Web.Optimization's dependencies

        */
        private bool ProjectLinkIsDirectDependency(XElement projectLink, IEnumerable<ProjectNugetPackage> packages)
        {
            var packageInfo = projectLink.GetTarget().Split(' ');
            var linkPackageName = packageInfo[0];
            var linkPackageVersion = packageInfo[1];

            foreach (var package in packages.Where(p => p.Project.Name.Equals(projectLink.GetSource(), StringComparison.InvariantCultureIgnoreCase)))
            {
                var dependencies = GetPackageDependencies(package.Package.Name, package.Package.Version);
                if (dependencies.Any(d => d.Name.Equals(linkPackageName, StringComparison.InvariantCultureIgnoreCase)
                                          &&
                                          d.Version.Equals(linkPackageVersion,
                                              StringComparison.InvariantCultureIgnoreCase)))
                {
                    return false;
                }
            }

            return true;
        }
        private XElement CreateNode(string name, string category, string label = null, string @group = null)
        {
            var labelAtt = label != null ? new XAttribute("Label", label) : null;
            var groupAtt = @group != null ? new XAttribute("Group", @group) : null;
            return new XElement(_dgmlns + "Node", new XAttribute("Id", name), labelAtt, groupAtt, new XAttribute("Category", category));
        }

        private XElement CreateLink(string source, string target, string category)
        {
            return new XElement(_dgmlns + "Link", new XAttribute("Source", source), new XAttribute("Target", target), new XAttribute("Category", category));
        }

        private XElement CreateStyle(string label, string color)
        {
            return new XElement(_dgmlns + "Style", new XAttribute("TargetType", "Node"), new XAttribute("GroupLabel", label), new XAttribute("ValueLabel", "True"),
                new XElement(_dgmlns + "Condition", new XAttribute("Expression", "HasCategory('" + label + "')")),
                new XElement(_dgmlns + "Setter", new XAttribute("Property", "Background"), new XAttribute("Value", color)));
        }

        #endregion

        #region Loading Configs

        private void LoadProjects()
        {
            XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";

            foreach (EnvDTE.Project project in _vsEnvironment.Solution.Projects)
            {
                if (!string.IsNullOrEmpty(project.FullName)
                    &&
                    !_projectExtensionExclusions.Any(ex => project.FullName.EndsWith(ex)))
                {
                    _projectList.Add(new Project {Path = project.FullName, Name = project.Name});
                }
            }
        }

        private void LoadPackageConfigs()
        {
            foreach (var pk in Directory.GetFiles(_solutionFolder, "packages.config", SearchOption.AllDirectories)
                .Where(pc => !pc.Contains(".nuget")))
            {
                var project = _projectList.SingleOrDefault(p => Path.GetDirectoryName(p.Path).Equals(Path.GetDirectoryName(pk), StringComparison.InvariantCultureIgnoreCase));
                if (project == null)
                {
                    ("Project not found in same folder than package " + pk).Dump();
                }
                else
                {
                    foreach (var pr in XDocument.Load(pk).Descendants("package"))
                    {
                        var package = GetOrCreatePackage(pr.Attribute("id").Value, pr.Attribute("version").Value);
                        if (!project.Packages.Any(p => p.Equals(package)))
                        {
                            project.Packages.Add(package);
                        }
                    }
                }
            }
        }

        #endregion

        #region Domain Objects

        private NugetPackage GetOrCreatePackage(string name, string version)
        {
            var p = _packageList.SingleOrDefault(l => l.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase) && 
            l.Version.Equals(version, StringComparison.InvariantCultureIgnoreCase));
            if (p == null) { p = new NugetPackage { Name = name, Version = version, PackageDependencies = GetPackageDependencies(name, version) }; _packageList.Add(p); }
            return p;
        }

        private IEnumerable<NugetPackage> GetPackageDependencies(string name, string version)
        {
            const string keyDelimiter = "@@@";
            var mapping = _packageList.ToDictionary(c => c.Name + keyDelimiter + c.Version, StringComparer.InvariantCultureIgnoreCase);
            var dependencies = new List<NugetPackage>();
            var nugetPackageFile = _solutionFolder + $@"\packages\{name}.{version}\{name}.{version}.nupkg";

            if (File.Exists(nugetPackageFile))
            {
                var package = new ZipPackage(nugetPackageFile);

                foreach (var dependency in package.GetCompatiblePackageDependencies(null))
                {
                    var key = mapping.Keys.SingleOrDefault(k => k.StartsWith(dependency.Id + keyDelimiter, StringComparison.InvariantCultureIgnoreCase));
                    if (key != null)
                    {
                        var dependentPackage = mapping[key];
                        dependencies.Add(new NugetPackage
                        {
                            Name = dependentPackage.Name,
                            Version = dependentPackage.Version
                        });
                    }
                }
            }

            return dependencies;
        }

        #endregion
    }
}
