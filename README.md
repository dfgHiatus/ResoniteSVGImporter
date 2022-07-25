# SVGImporter

A [NeosModLoader](https://github.com/zkxs/NeosModLoader) mod for [Neos VR](https://neos.com/) that enables the import of SVG (Scalable Vector Graphics) files as 3D models.

## Installation
1. Install [NeosModLoader](https://github.com/zkxs/NeosModLoader).
1. Place [SVGImporter.dll](https://github.com/dfgHiatus/NeosSVGImporter/releases/download/v1.0.0/SVGImporter.dll) into your `nml_mods` folder. This folder should be at `C:\Program Files (x86)\Steam\steamapps\common\NeosVR\nml_mods` for a default install. You can create it if it's missing, or if you launch the game once with NeosModLoader installed it will create the folder for you.
1. If you do not have [Blender](https://www.blender.org/download/) installed to your C Drive (or Linux equivalent), install and extract it to your Neos tools folder. This can be found under `C:\Program Files (x86)\Steam\steamapps\common\NeosVR\tools\Blender` for a default install.
1. Start the game. If you want to verify that the mod is working you can check your Neos logs.

Tested only on Windows, should work on Linux. Depending on the size of the file, it may take up to a few minutes to fully import. It's OK if the import dialogue seems to hang on "Preimporting" for a little bit - this is where the bulk of the conversion is done.
