const AdmZip = require("adm-zip");
const path  = require("path");
const fs = require("fs");
const pkg = require("pkg");
const { execSync } = require("child_process");
const packageJson = JSON.parse(fs.readFileSync("./package.json", "utf8"))
const { exit } = require("process")

const buildCSharpBridge = () => {
    console.log("Building C# LHMBridge...")
    const csharpProjectPath = path.join(__dirname, "..", "csharp", "LHMBridge")

    try {
        execSync(
            `dotnet publish "${csharpProjectPath}" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`,
            { stdio: "inherit" }
        )
        console.log("C# LHMBridge built successfully")
    } catch (error) {
        console.error("Failed to build C# LHMBridge:", error.message)
        exit(1)
    }
}

const getCSharpOutputPath = () => {
    return path.join(
        __dirname, "..", "csharp", "LHMBridge",
        "bin", "Release", "net10.0", "win-x64", "publish", "LHMBridge.exe"
    )
}

const build = async(platform) => {
    fs.mkdirSync(`./base/${platform}`)
    fs.copyFileSync("./base/entry.tp", `./base/${platform}/entry.tp`)
    fs.copyFileSync("./base/plugin_icon.png", `./base/${platform}/${packageJson.name}.png`)

    // Copy LHMBridge.exe for Windows builds
    if (platform === "Windows") {
        const lhmBridgePath = getCSharpOutputPath()
        if (fs.existsSync(lhmBridgePath)) {
            fs.copyFileSync(lhmBridgePath, `./base/${platform}/LHMBridge.exe`)
            console.log("Copied LHMBridge.exe to build folder")
        } else {
            console.error("LHMBridge.exe not found at:", lhmBridgePath)
            exit(1)
        }
    }

    let nodeVersion = 'node16-win-x64'
    let execName = `${packageJson.name}.exe`

    if( platform != "Windows" ) {
        execName = packageJson.name
    }

    if( platform == "MacOS") {
        nodeVersion = 'node16-macos-x64'

    }
    if( platform == "MacOS-Arm64") {
        nodeVersion = '???'
    }
    if( platform == "Linux") {
        nodeVersion = 'node16-linux-x64'
    }

    console.log("Running pkg")
    await pkg.exec([
      "--targets",
      nodeVersion,
      "--output",
      `base/${platform}/${execName}`,
      ".",
    ]);

    console.log("Running Zip File Creation")
    const zip = new AdmZip()
    zip.addLocalFolder(
      path.normalize(`./base/${platform}/`),
      packageJson.name
    );

    zip.writeZip(path.normalize(`./Installers/${packageJson.name}-${platform}-${packageJson.version}.tpp`))

    console.log("Cleaning Up")
    fs.unlinkSync(`./base/${platform}/entry.tp`)
    fs.unlinkSync(`./base/${platform}/${packageJson.name}.png`)
    fs.unlinkSync(`./base/${platform}/${execName}`)
    if (platform === "Windows" && fs.existsSync(`./base/${platform}/LHMBridge.exe`)) {
        fs.unlinkSync(`./base/${platform}/LHMBridge.exe`)
    }
    fs.rmdirSync(`./base/${platform}`)
}

const cleanInstallers  = () => {
    try {
        dirPath = './Installers/'
        // Read the directory given in `path`
        const files = fs.readdir(dirPath, (err, files) => {
          if (err)
            throw err;

          files.forEach((file) => {
            // Check if the file is with a PDF extension, remove it
            if (file.split('.').pop().toLowerCase() == 'tpp') {
              console.log(`Deleting file: ${file}`);
              fs.unlinkSync(dirPath + file)
            }
          });
        });
      } catch (err) {
        console.error(err);
      }
}

const executeBuilds= async () => {
    cleanInstallers()

    // Build C# bridge first (Windows only)
    buildCSharpBridge()

    await build("Windows")
    //await build("MacOS")
    //await build("Linux")
}

executeBuilds();
