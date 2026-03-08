using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Assimp;
using DRV3_Sharp_Library.Formats.Data.SRD;
using DRV3_Sharp_Library.Formats.Data.SRD.Resources;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tga;
using AssimpNode = Assimp.Node;
using Node = DRV3_Sharp_Library.Formats.Data.SRD.Blocks.Node;

namespace DRV3_Sharp.Menus;

internal sealed class SrdMenu : IMenu
{
    private SrdData? loadedData = null;
    private FileInfo? loadedDataInfo = null;
    public string HeaderText => "You can choose from the following options:";
    public int FocusedEntry { get; set; }

    public MenuEntry[] AvailableEntries
    {
        get
        {
            List<MenuEntry> entries = new()
            {
                // Add always-available entries
                new("Load", "Load an SRD file, pulling data from associated SRDI and SRDV binary files.", Load),
                new("Batch Export Textures", "Export textures from multiple SRD files in a specified directory.", BatchExportTextures),
                new("Create SRD from Image", "Create an SRD file from a single image (BMP, PNG, or TGA).", CreateSrdFromImage),
                new("Replace Texture in loaded SRD", "Replace an existing texture in the currently loaded SRD with a new image.", ReplaceTexture),
                new("Help", "View descriptions of currently-available operations.", Help),
                new("Back", "Return to the previous menu.", Program.PopMenu),
                new("Exit", "Exits the program.", Program.ClearMenuStack)
            };

            if (loadedData is not null)
            {
                // Add loaded-data specific entries
                entries.Insert(0, new("Export Textures", "Export all texture resources within the resource data.", ExtractTextures));
                entries.Insert(1, new("Export Models", "Export all 3D geometry within the resource data.", ExtractModels));
            }

            return entries.ToArray();
        }
    }

    private void Load()
    {
        var paths = Utils.ParsePathsFromConsole("Type the file you wish to load, or drag-and-drop it onto this window: ", true, false);
        if (paths?.Length == 0 || paths?[0] is not FileInfo fileInfo)
        {
            Console.Write("Unable to find the path specified.");
            Utils.PromptForEnterKey(false);
            return;
        }

        // TODO: Check with the user if there are existing loaded files before clearing the list
        loadedData = null;
        loadedDataInfo = null;

        // Determine the expected paths of the accompanying SRDI and SRDV files.
        int lengthNoExtension = (fileInfo.FullName.Length - fileInfo.Extension.Length);
        string noExtension = fileInfo.FullName[..lengthNoExtension];
        FileInfo srdiInfo = new(noExtension + ".srdi");
        FileInfo srdvInfo = new(noExtension + ".srdv");

        // Initialize appropriate FileStreams based on which files exist.
        using FileStream fs = new(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
        FileStream? srdi = null;
        if (srdiInfo.Exists) srdi = new FileStream(srdiInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
        FileStream? srdv = null;
        if (srdvInfo.Exists) srdv = new FileStream(srdvInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Deserialize the SRD data with available resource streams
        SrdSerializer.Deserialize(fs, srdi, srdv, out SrdData data);
        loadedData = data;
        loadedDataInfo = fileInfo;

        Console.Write($"Loaded the SRD file successfully.");
        Utils.PromptForEnterKey(false);
    }

    private void BatchExportTextures()
    {
        var paths = Utils.ParsePathsFromConsole("Type the directory containing SRD files you want to extract textures from, or drag-and-drop it onto this window: ", true, true);
        if (paths?.Length == 0 || paths?[0] is not DirectoryInfo directoryInfo)
        {
            Console.Write("Unable to find the directory specified.");
            Utils.PromptForEnterKey(false);
            return;
        }

        int totalFilesProcessed = 0;
        int totalTexturesExported = 0;

        var srdFiles = directoryInfo.GetFiles("model.srd", SearchOption.AllDirectories);

        foreach (var fileInfo in srdFiles)
        {
            totalFilesProcessed++;
            Console.WriteLine($"Processing SRD file: {fileInfo.FullName}");

            SrdData? currentSrdData = null;

            try
            {
                // Determine the expected paths of the accompanying SRDI and SRDV files.
                int lengthNoExtension = (fileInfo.FullName.Length - fileInfo.Extension.Length);
                string noExtension = fileInfo.FullName[..lengthNoExtension];
                FileInfo srdiInfo = new(noExtension + ".srdi");
                FileInfo srdvInfo = new(noExtension + ".srdv");

                // Initialize appropriate FileStreams based on which files exist.
                using FileStream fs = new(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                FileStream? srdi = null;
                if (srdiInfo.Exists) srdi = new FileStream(srdiInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                FileStream? srdv = null;
                if (srdvInfo.Exists) srdv = new FileStream(srdvInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);

                // Deserialize the SRD data with available resource streams
                SrdSerializer.Deserialize(fs, srdi, srdv, out currentSrdData);

                if (currentSrdData != null)
                {
                    totalTexturesExported += ExportTexturesFromSrdData(currentSrdData, fileInfo);
                }
                Console.WriteLine($"Successfully processed {fileInfo.Name}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing {fileInfo.Name}: {ex.Message}");
            }
        }

        Console.WriteLine("\nPerforming post-processing: Copying main character textures to 'bmp' folder...");
        string bmpOutputDir = Path.Combine(directoryInfo.FullName, "bmp");
        Directory.CreateDirectory(bmpOutputDir);

        var allBmpFiles = directoryInfo.GetFiles("*.bmp", SearchOption.AllDirectories);
        int copiedCount = 0;

        foreach (var bmpFile in allBmpFiles)
        {
            // Skip files already in the output directory to avoid infinite recursion or errors
            if (bmpFile.DirectoryName!.Equals(bmpOutputDir, StringComparison.OrdinalIgnoreCase)) continue;

            string fileName = bmpFile.Name;

            // Exclude unwanted files: chara_black.bmp and normal maps (*n.bmp)
            if (fileName.Equals("chara_black.bmp", StringComparison.OrdinalIgnoreCase)) continue;
            if (fileName.EndsWith("n.bmp", StringComparison.OrdinalIgnoreCase)) continue;

            // Check if it matches the pattern stand_NUMBER_NUMBER.bmp
            if (Regex.IsMatch(fileName, @"^stand_\d+_\d+\.bmp$", RegexOptions.IgnoreCase))
            {
                try
                {
                    bmpFile.CopyTo(Path.Combine(bmpOutputDir, fileName), true);
                    copiedCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error copying {fileName}: {ex.Message}");
                }
            }
        }

        Console.Write($"\nFinished batch export. Processed {totalFilesProcessed} SRD files and exported {totalTexturesExported} textures. {copiedCount} main textures were copied to the 'bmp' folder.");
        Utils.PromptForEnterKey(false);
    }

    private void ExtractTextures()
    {
        if (loadedData is null || loadedDataInfo is null) return;
        var successfulExports = ExportTexturesFromSrdData(loadedData, loadedDataInfo);
        Console.Write($"Exported {successfulExports} textures successfully.");
        Utils.PromptForEnterKey(false);
    }

    public static void ExportTexturesFromSrd(string srdPath)
    {
        try
        {
            FileInfo fileInfo = new(srdPath);
            if (!fileInfo.Exists) return;

            // Determine the expected paths of the accompanying SRDI and SRDV files.
            int lengthNoExtension = (fileInfo.FullName.Length - fileInfo.Extension.Length);
            string noExtension = fileInfo.FullName[..lengthNoExtension];
            FileInfo srdiInfo = new(noExtension + ".srdi");
            FileInfo srdvInfo = new(noExtension + ".srdv");

            // Initialize appropriate FileStreams based on which files exist.
            using FileStream fs = new(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            FileStream? srdi = null;
            if (srdiInfo.Exists) srdi = new FileStream(srdiInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            FileStream? srdv = null;
            if (srdvInfo.Exists) srdv = new FileStream(srdvInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Deserialize the SRD data with available resource streams
            SrdSerializer.Deserialize(fs, srdi, srdv, out SrdData data);

            ExportTexturesFromSrdData(data, fileInfo);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during automatic SRD texture extraction: {ex.Message}");
        }
    }

    internal static int ExportTexturesFromSrdData(SrdData data, FileInfo dataInfo)
    {
        var successfulExports = 0;
        foreach (var resource in data.Resources)
        {
            if (resource is not TextureResource texture) continue;

            string outputPath = Path.Combine(dataInfo.DirectoryName!, texture.Name);

            try
            {
                using FileStream fs = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                if (texture.Name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                {
                    texture.ImageMipmaps[0].Save(fs, new TgaEncoder());
                }
                else if (texture.Name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                {
                    texture.ImageMipmaps[0].Save(fs, new BmpEncoder());
                }
                else if (texture.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    texture.ImageMipmaps[0].Save(fs, new PngEncoder());
                }
                else
                {
                    // Default to PNG if extension is unknown or missing
                    Console.WriteLine($"Warning: Unknown texture format for {texture.Name}. Attempting to save as PNG.");
                    texture.ImageMipmaps[0].Save(fs, new PngEncoder());
                }
                ++successfulExports;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error exporting texture {texture.Name} from {dataInfo.Name}: {ex.Message}");
            }
        }
        return successfulExports;
    }

    private void ExtractModels()
    {
        // Loop through the various resources in the SRD and construct an Assimp scene
        // and associated meshes, trees, etc.

        // First, let's populate some lists of the various resources we will use.
        List<MaterialResource> materialResources = new();
        List<MeshResource> meshResources = new();
        List<TextureInstanceResource> textureInstanceResources = new();
        List<VertexResource> vertexResources = new();
        foreach (var resource in loadedData!.Resources)
        {
            switch (resource)
            {
                case MaterialResource mat:
                    materialResources.Add(mat);
                    break;
                case MeshResource mesh:
                    meshResources.Add(mesh);
                    break;
                case TextureInstanceResource txi:
                    textureInstanceResources.Add(txi);
                    break;
                case VertexResource vertex:
                    vertexResources.Add(vertex);
                    break;
            }
        }

        // Second, let's generate our material data from MAT and TXI resources.
        List<Material> constructedMaterials = new();
        foreach (MaterialResource materialResource in materialResources)
        {
            Material constructedMaterial = new();
            constructedMaterial.Name = materialResource.Name;

            foreach ((string mapName, string textureName) in materialResource.MapTexturePairs)
            {
                // Find the TXI resource associated with the current map
                var matchingTexture = textureInstanceResources.First(txi => txi.LinkedMaterialName == textureName);

                TextureSlot texSlot = new()
                {
                    FilePath = matchingTexture.LinkedTextureName,
                    Mapping = TextureMapping.FromUV,
                    UVIndex = 0,
                };

                // Determine map type
                if (mapName.StartsWith("COLORMAP"))
                {
                    if (matchingTexture.LinkedTextureName.StartsWith("lm"))
                        texSlot.TextureType = TextureType.Lightmap;
                    else
                        texSlot.TextureType = TextureType.Diffuse;
                }
                else if (mapName.StartsWith("NORMALMAP"))
                {
                    texSlot.TextureType = TextureType.Normals;
                }
                else if (mapName.StartsWith("SPECULARMAP"))
                {
                    texSlot.TextureType = TextureType.Specular;
                }
                else if (mapName.StartsWith("TRANSPARENCYMAP"))
                {
                    texSlot.TextureType = TextureType.Opacity;
                }
                else if (mapName.StartsWith("REFLECTMAP"))
                {
                    texSlot.TextureType = TextureType.Reflection;
                }
                else
                {
                    Console.WriteLine($"WARNING: Texture map type {mapName} is not currently supported.");
                }
                texSlot.TextureIndex = constructedMaterial.GetMaterialTextureCount(texSlot.TextureType);

                if (!constructedMaterial.AddMaterialTexture(texSlot))
                    Console.WriteLine($"WARNING: Adding map ({mapName}, {textureName}) did not update or create new data!");
            }

            constructedMaterials.Add(constructedMaterial);
        }

        // Third, let's generate our 3D geometry meshes, from MSH resources and their associated VTX resources.
        List<Mesh> constructedMeshes = new();
        if (meshResources.Count != vertexResources.Count)
        {
            //throw new InvalidDataException("The number of meshes did not match the number of vertices.");
        }

        // Iterate through the meshes and construct Assimp meshes based on them and their vertices.
        foreach (MeshResource meshResource in meshResources)
        {
            VertexResource linkedVertexResource = vertexResources.First(r => r.Name == meshResource.LinkedVertexName);

            Mesh assimpMesh = new();
            assimpMesh.Name = meshResource.Name;
            assimpMesh.PrimitiveType = PrimitiveType.Triangle;
            assimpMesh.MaterialIndex = constructedMaterials.IndexOf(constructedMaterials.First(mat =>
                mat.Name == meshResource.LinkedMaterialName));

            foreach (var vertex in linkedVertexResource.Vertices)
            {
                assimpMesh.Vertices.Add(new Vector3D(vertex.X, vertex.Y, vertex.Z));
            }

            foreach (var normal in linkedVertexResource.Normals)
            {
                assimpMesh.Normals.Add(new Vector3D(normal.X, normal.Y, normal.Z));
            }

            foreach (var index in linkedVertexResource.Indices)
            {
                Face face = new();
                face.Indices.Add(index.Item1);
                face.Indices.Add(index.Item2);
                face.Indices.Add(index.Item3);
                assimpMesh.Faces.Add(face);
            }

            assimpMesh.UVComponentCount[0] = 2;
            assimpMesh.TextureCoordinateChannels[0] = new();
            foreach (var uv in linkedVertexResource.TextureCoords)
            {
                Vector3D texCoord3d = new(uv.X, uv.Y, 0.0f);
                assimpMesh.TextureCoordinateChannels[0].Add(texCoord3d);
            }

            // Finally add the constructed Assimp mesh to the list.
            constructedMeshes.Add(assimpMesh);
        }

        Scene scene = new();
        SceneResource? sceneResource = loadedData!.Resources.First(r => r is SceneResource) as SceneResource;
        if (sceneResource is null)
        {
            Console.Write("ERROR: The current SRD file contains no scene, meaning it does not contain any 3D model data.");
            Utils.PromptForEnterKey();
            return;
        }

        scene.Clear();
        scene.RootNode = new AssimpNode(sceneResource.Name);
        scene.Meshes.AddRange(constructedMeshes);
        scene.Materials.AddRange(constructedMaterials);

        foreach (var treeName in sceneResource.LinkedTreeNames)
        {
            TreeResource tree = loadedData!.Resources.First(r => r is TreeResource tre && tre.Name == treeName) as TreeResource ?? throw new InvalidOperationException();

            // Perform a depth-first traverse through the tree to create all the Assimp nodes.
            AssimpNode treeRoot = DepthFirstTreeNodeConversion(tree.RootNode, meshResources);

            scene.RootNode.Children.Add(treeRoot);
        }

        AssimpContext context = new();
        var exportFormats = context.GetSupportedExportFormats();
        foreach (var format in exportFormats)
        {
            if (format.FileExtension != "gltf") continue;

            string exportName = $"{loadedDataInfo!.FullName}";
            context.ExportFile(scene, $"{exportName}.{format.FileExtension}", format.FormatId);
            break;
        }

        Console.Write("Successfully exported 3D model.");
        Utils.PromptForEnterKey(false);
    }

    private AssimpNode DepthFirstTreeNodeConversion(Node inputNode, List<MeshResource> meshResources)
    {
        AssimpNode outputNode = new(inputNode.Name);

        for (var meshNum = 0; meshNum < meshResources.Count; ++meshNum)
        {
            if (meshResources[meshNum].Name == inputNode.Name)
            {
                outputNode.MeshIndices.Add(meshNum);
            }
        }

        // Shortcut out if we have reached a leaf node.
        if (inputNode.Children is null) return outputNode;

        // Recursively traverse all child nodes.
        foreach (var child in inputNode.Children)
        {
            outputNode.Children.Add(DepthFirstTreeNodeConversion(child, meshResources));
        }

        return outputNode;
    }

    private void CreateSrdFromImage()
    {
        var paths = Utils.ParsePathsFromConsole("Type the image file (BMP, PNG, TGA) you wish to convert to SRD, or drag-and-drop it onto this window: ", true, false);
        if (paths?.Length == 0 || paths?[0] is not FileInfo imageInfo)
        {
            Console.Write("Unable to find the path specified.");
            Utils.PromptForEnterKey(false);
            return;
        }

        // Ask for the output directory to prevent placing files in 'extract' or 'orig'
        var outputPaths = Utils.ParsePathsFromConsole("Type the output directory for the new SRD files (e.g., inside partition_data_win_mod), or drag-and-drop it: ", false, true);
        if (outputPaths?.Length == 0 || outputPaths?[0] is not DirectoryInfo outputDir)
        {
            Console.Write("Invalid output directory.");
            Utils.PromptForEnterKey(false);
            return;
        }

        if (!outputDir.Exists) outputDir.Create();

        try
        {
            // 1. Import the texture from the image file
            using FileStream imageStream = new(imageInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            var textureResource = ResourceSerializer.ImportTexture(imageInfo.Name, imageStream);

            // 2. Wrap it in SrdData
            SrdData srdData = new(new List<ISrdResource> { textureResource });

            // 3. Prepare output paths
            string outputSrd = Path.Combine(outputDir.FullName, "model.srd");
            string outputSrdi = Path.Combine(outputDir.FullName, "model.srdi");
            string outputSrdv = Path.Combine(outputDir.FullName, "model.srdv");

            Console.WriteLine($"Creating SRD files at {outputDir.FullName}...");

            // 4. Serialize to SRD/SRDI/SRDV
            using FileStream srdFs = new(outputSrd, FileMode.Create, FileAccess.Write, FileShare.None);
            using FileStream srdiFs = new(outputSrdi, FileMode.Create, FileAccess.Write, FileShare.None);
            using FileStream srdvFs = new(outputSrdv, FileMode.Create, FileAccess.Write, FileShare.None);

            SrdSerializer.Serialize(srdData, srdFs, srdiFs, srdvFs);

            Console.Write($"Successfully created SRD files: model.srd, model.srdi, model.srdv in the mod directory.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating SRD from image: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }

        Utils.PromptForEnterKey(false);
    }

    private void ReplaceTexture()
    {
        if (loadedData is null || loadedDataInfo is null)
        {
            Console.Write("No SRD file is currently loaded. Please load an SRD file first.");
            Utils.PromptForEnterKey(false);
            return;
        }

        // 1. List available textures in the loaded SRD
        var textures = loadedData.Resources.OfType<TextureResource>().ToList();
        if (textures.Count == 0)
        {
            Console.Write("No textures found in the loaded SRD.");
            Utils.PromptForEnterKey(false);
            return;
        }

        Console.WriteLine("Available textures in loaded SRD:");
        for (int i = 0; i < textures.Count; i++)
        {
            Console.WriteLine($"{i}: {textures[i].Name}");
        }

        Console.Write("Enter the index of the texture you wish to replace: ");
        if (!int.TryParse(Console.ReadLine(), out int textureIndex) || textureIndex < 0 || textureIndex >= textures.Count)
        {
            Console.Write("Invalid index.");
            Utils.PromptForEnterKey(false);
            return;
        }

        // 2. Select the new image file
        var paths = Utils.ParsePathsFromConsole("Type the new image file (BMP, PNG, TGA), or drag-and-drop it onto this window: ", true, false);
        if (paths?.Length == 0 || paths?[0] is not FileInfo imageInfo)
        {
            Console.Write("Unable to find the path specified.");
            Utils.PromptForEnterKey(false);
            return;
        }

        try
        {
            // 3. Import the new image
            using FileStream imageStream = new(imageInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            var original = textures[textureIndex];
            var imported = ResourceSerializer.ImportTexture(original.Name, imageStream);

            // Create a new texture resource that preserves the original's metadata
            var newTexture = imported with
            {
                Format = original.Format,
                TxrUnknown00 = original.TxrUnknown00,
                TxrUnknown0D = original.TxrUnknown0D,
                Swizzle = original.Swizzle,
                RsiUnknown00 = original.RsiUnknown00,
                RsiUnknown01 = original.RsiUnknown01,
                RsiUnknown02 = original.RsiUnknown02,
                RsiUnknown06 = original.RsiUnknown06
            };

            // 4. Replace the resource in the loaded data
            int resourceIndex = loadedData.Resources.IndexOf(original);
            loadedData.Resources[resourceIndex] = newTexture;

            // 5. Ask for the output directory to prevent overwriting originals
            var outputPaths = Utils.ParsePathsFromConsole("Type the output directory for the updated SRD files (e.g., inside partition_data_win_mod), or drag-and-drop it: ", false, true);
            if (outputPaths?.Length == 0 || outputPaths?[0] is not DirectoryInfo outputDir)
            {
                Console.Write("Invalid output directory.");
                Utils.PromptForEnterKey(false);
                return;
            }

            if (!outputDir.Exists) outputDir.Create();

            // 6. Save the updated SRD to the new location
            string baseName = Path.GetFileNameWithoutExtension(loadedDataInfo.Name);
            string outputSrd = Path.Combine(outputDir.FullName, baseName + ".srd");
            string outputSrdi = Path.Combine(outputDir.FullName, baseName + ".srdi");
            string outputSrdv = Path.Combine(outputDir.FullName, baseName + ".srdv");

            Console.WriteLine($"Saving updated SRD files to {outputDir.FullName}...");
            using FileStream srdFs = new(outputSrd, FileMode.Create, FileAccess.Write, FileShare.None);
            using FileStream srdiFs = new(outputSrdi, FileMode.Create, FileAccess.Write, FileShare.None);
            using FileStream srdvFs = new(outputSrdv, FileMode.Create, FileAccess.Write, FileShare.None);

            SrdSerializer.Serialize(loadedData, srdFs, srdiFs, srdvFs);

            Console.Write("Successfully replaced texture and updated SRD files in the mod directory.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error replacing texture: {ex.Message}");
        }

        Utils.PromptForEnterKey(false);
    }

    private void Help()
    {
        Utils.PrintMenuDescriptions(AvailableEntries);
    }
}
