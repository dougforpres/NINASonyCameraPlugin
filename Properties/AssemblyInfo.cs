﻿using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// [MANDATORY] The following GUID is used as a unique identifier of the plugin. Generate a fresh one for your plugin!
[assembly: Guid("f3fd7bb5-2b69-40cc-846f-4f4a2ff62518")]

// [MANDATORY] The assembly versioning
//Should be incremented for each new release build of a plugin
[assembly: AssemblyVersion("1.0.0.1")]
[assembly: AssemblyFileVersion("1.0.0.1")]

// [MANDATORY] The name of your plugin
[assembly: AssemblyTitle("Sony Camera Plugin")]
// [MANDATORY] A short description of your plugin
[assembly: AssemblyDescription("Add support for Sony Cameras")]

// The following attributes are not required for the plugin per se, but are required by the official manifest meta data

// Your name
[assembly: AssemblyCompany("Doug Henderson")]
// The product name that this plugin is part of
[assembly: AssemblyProduct("Sony Camera Plugin")]
[assembly: AssemblyCopyright("Copyright © 2023 Doug Henderson")]

// The minimum Version of N.I.N.A. that this plugin is compatible with
[assembly: AssemblyMetadata("MinimumApplicationVersion", "2.0.0.9001")]

// The license your plugin code is using
[assembly: AssemblyMetadata("License", "MPL-2.0")]
// The url to the license
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
// The repository where your pluggin is hosted
[assembly: AssemblyMetadata("Repository", "https://github.com/dougforpres/NINASonyCameraPlugin")]

// The following attributes are optional for the official manifest meta data

//[Optional] Your plugin homepage URL - omit if not applicaple
[assembly: AssemblyMetadata("Homepage", "https://retro.kiwi")]

//[Optional] Common tags that quickly describe your plugin
[assembly: AssemblyMetadata("Tags", "Sony,Camera")]

//[Optional] A link that will show a log of all changes in between your plugin's versions
[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/dougforpres/NINASonyCameraPlugin/blob/master/CHANGELOG.md")]

//[Optional] The url to a featured logo that will be displayed in the plugin list next to the name
[assembly: AssemblyMetadata("FeaturedImageURL", "https://retro.kiwi/images/NINAPluginLogoV2.jpg")]
//[Optional] A url to an example screenshot of your plugin in action
[assembly: AssemblyMetadata("ScreenshotURL", "")]
//[Optional] An additional url to an example example screenshot of your plugin in action
[assembly: AssemblyMetadata("AltScreenshotURL", "")]
//[Optional] An in-depth description of your plugin
[assembly: AssemblyMetadata("LongDescription", @"This plugin adds the ability to image using compatible Sony cameras.

It uses the same backend code as the Sony ASCOM driver, but because it is more 'native' to N.I.N.A, performance is improved and additional features are available:

* LiveView is available for any Sony Camera that supports it
* The Gain property displays ISO values instead of cryptic numbers
* Images are downloaded in the native ARW format

*Note*: This addon uses the core (non-ASCOM) files present in the Sony ASCOM driver v1.0.1.17 and later, which must be installed for this addon to start up correctly. You can find the Sony ASCOM driver at [https://github.com/dougforpres/ASCOMSonyCameraDriver/releases](https://github.com/dougforpres/ASCOMSonyCameraDriver/releases)")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]
// [Unused]
[assembly: AssemblyConfiguration("")]
// [Unused]
[assembly: AssemblyTrademark("")]
// [Unused]
[assembly: AssemblyCulture("")]
