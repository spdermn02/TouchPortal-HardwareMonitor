const wmi = require("node-wmi")
const TouchPortalApi = require("touchportal-api")
const { open } = require("out-url")
const Constants = require('./consts')

// Create an instance of the Touch Portal Client
const TPClient = new TouchPortalApi.Client()

// Define a pluginId, matches your entry.tp file
const pluginId = Constants.pluginId

let prevCaptureInterval = 2000  //ms capture time interval

const hardware  = {}
const pluginSettings = {
  [Constants.CAPTURE_INTERVAL_SETTING] : prevCaptureInterval,
  [Constants.TEMP_READOUT_SETTING]: 'C',
  [Constants.NORMALIZE_THROUGHPUT]: 'No',
  [Constants.NORMALIZE_DATA]: 'Yes'
}
let firstRun = 1
let waitTime = 1000
let MAX_WAIT_TIME
let sensorCapture = undefined

const buildHardwareList = () => {
  const hardwareTypes = {}
  TPClient.logIt("DEBUG","Attempting to select for data source",pluginSettings[Constants.SENSOR_DATA_SOURCE],"class Hardware" )
  wmi.Query(
    {
      namespace: pluginSettings[Constants.SENSOR_DATA_SOURCE],
      class: "Hardware",
    },
    function (err, hardwareData) {
      if( err ) {
        TPClient.logIt('ERROR','An error has occurred reading hardware data:', err, 'will try again')
        if ( waitTime > Constants.MAX_WAIT_TIME ) {
            TPClient.logIt('ERROR','attempted to read hardware data failed 60 times, check if your Hardware Monitor is running')
            return
        }
        setTimeout(() => { buildHardwareList() }, waitTime);
        waitTime = waitTime + 1000
        return
      }
      if( typeof hardwareData === 'object' ) {
          for( let i = 0; i < hardwareData.length; i++ ){
              const key = hardwareData[i].Identifier
              if( hardware[key] === undefined ) {
                hardware[key] = hardwareData[i]
                hardware[key].HardwareType = hardware[key].HardwareType.toLowerCase().replace(/gpu.*/,'GPU').toUpperCase()
                hardware[key].Index = parseInt(hardware[key].Identifier.toLowerCase().replace(/.*\/([0-9]+)/,'$1'),10)
                hardwareTypes[hardware[key].HardwareType] = hardwareTypes[hardware[key].HardwareType] != undefined ? hardwareTypes[hardware[key].HardwareType] + 1 : 1;
                if( hardware[key].Index == null || isNaN(hardware[key].Index) ) {
                  hardware[key].Index = hardwareTypes[hardware[key].HardwareType]
                }
                
                hardware[key].Sensors = {}
              }
          }
          
      }
      startCapture()
    }
  );
  return
}

const buildSensorStateId = (hardwareKey, sensorInfo) => {
  if( hardware[hardwareKey] == null ) {
    TPClient.logIt("ERROR","Unknown hardware key present", hardwareKey,"and hasn't been seen before")
    return { id: 'unknown', desc:'unknown', defaultValue: '0', parentGroup: 'UNKNOWN'};
  }
    const sensorType = sensorInfo.SensorType
    const sensorName = sensorInfo.Name.toLowerCase().replace(/ /g,'.').replace(/#/g,'')
    const sensorNameStr = sensorInfo.Name
    const indexNum = hardware[hardwareKey].Index == null || isNaN(hardware[hardwareKey].Index) ? '' : hardware[hardwareKey].Index
    const sensorStateId = `tp-hm.state.${hardware[hardwareKey].HardwareType}${indexNum}.${sensorType}.${sensorName}`
    let parentGroup = `${hardware[hardwareKey].HardwareType}`
    parentGroup = indexNum !== ''  ? `${parentGroup} ${indexNum}` : `${parentGroup}`
    parentGroup = `${parentGroup} - ${hardware[hardwareKey].Name}`
    const sensorStateDesc = `${parentGroup} - ${sensorType} - ${sensorNameStr}`
    return { id: sensorStateId, desc: sensorStateDesc, defaultValue: '0', parentGroup: `${parentGroup}` };
}

const runSensorConversions = (sensor) => {
  if( sensor.SensorType === 'Temperature' && pluginSettings[Constants.TEMP_READOUT_SETTING] === 'F') {
    sensor.Value = (sensor.Value * 9.0 / 5.0 ) + 32.0
  }
  else if( sensor.SensorType === 'Throughput' && pluginSettings[Constants.NORMALIZE_THROUGHPUT].toLowerCase() === 'yes') {
    let currValue = sensor.Value
    let count = 0
    while( currValue > 1024.0 ) {
      currValue = currValue / 1024.0
      count++
    }
    const unit = getThroughputUnit(count)
    sensor.Value = currValue
    sensor.Unit = unit
  }
  else if( sensor.SensorType === 'SmallData' && pluginSettings[Constants.NORMALIZE_DATA].toLowerCase() === 'yes') {
    let currValue = sensor.Value
    let count = 2 //Start at 2 because Libre Hardware Monitor already reads these as MB at minimum - TODO: Check Open Hardware Monitor
    while( currValue > 1024.0 ) {
      currValue = currValue / 1024.0
      count++
    }
    const unit = getUnit(count)
    sensor.Value = currValue;
    sensor.Unit = unit
    console.log(JSON.stringify(sensor))
  }
}
const getUnit = (count) => {
  let unitScale = ""
  switch(count) {
    case 3: unitScale="G"; break;
    case 2: unitScale="M"; break;
    case 1: unitScale="K"; break;
  }
  return unitScale+"B";
}

const getThroughputUnit = (count) => {
  return getUnit(count)+"/s"
}

const startCapture = () => {
  if( sensorCapture ) {
    clearInterval(sensorCapture)
  }
  sensorCapture = setInterval( () => { 
    let sensorStateArray = [];
    let stateUpdateArray = [];
    wmi.Query(
      {
        namespace: pluginSettings[Constants.SENSOR_DATA_SOURCE],
        class: "Sensor",
      },
      function (err, sensorData) {
        if( err ) {
          TPClient.logIt('ERROR','An error has occurred reading sensor data:', err)
          return
        }
        if( typeof sensorData === 'object' ) {
            for(const sensor of sensorData){
                const hardwareKey = sensor.Parent
                const stateId = buildSensorStateId(hardwareKey, sensor)
                sensor.StateId = stateId
                // Apparently some sensors use the same Identifier, so throwing Name on them
                // To force uniqueness
                sensor.Identifier = sensor.Identifier + "/" + sensor.Name.toLowerCase().replace(/ /g,'.')
                
                runSensorConversions(sensor)

                sensor.Value = sensor.Value % 1 !== 0 ? parseFloat(sensor.Value).toFixed(1) : sensor.Value

                if( hardware[hardwareKey].Sensors[sensor.Identifier] == undefined ) {
                    sensor.StateId.defaultValue  = sensor.Value;
                    hardware[hardwareKey].Sensors[sensor.Identifier] = sensor
                    //createStateArray
                    sensorStateArray.push(sensor.StateId)

                    //updateStateArray - even though we send defaultValue, we need this so any Events are fired
                    stateUpdateArray.push({'id': sensor.StateId.id, 'value': sensor.Value})
                    if( sensor.Unit !== undefined ) {
                      let unitSensor = {
                        id: sensor.StateId.id+".unit",
                        desc: sensor.StateId.desc + " Unit",
                        defaultValue: "UKN/s",
                        parentGroup: sensor.StateId.parentGroup
                      };
                      sensorStateArray.push(unitSensor)
                      stateUpdateArray.push({'id': sensor.StateId.id+".unit", 'value': sensor.Unit})
                    }
                }
                else{
                    if( hardware[hardwareKey].Sensors[sensor.Identifier].Value !== sensor.Value ){
                        hardware[hardwareKey].Sensors[sensor.Identifier] = sensor
                        //addToStateUpdateArray
                        stateUpdateArray.push({'id': sensor.StateId.id, 'value': sensor.Value})
                        if( sensor.Unit !== undefined ) {
                          stateUpdateArray.push({'id': sensor.StateId.id+".unit", 'value': sensor.Unit})
                        }
                    }
                }
            }

            if( sensorStateArray.length > 0 ) {
              TPClient.createStateMany(sensorStateArray)
            }
            if( stateUpdateArray.length > 0 ) {
              TPClient.stateUpdateMany(stateUpdateArray)
            }
        }
      }
    )},prevCaptureInterval)
}

TPClient.on("Settings", (data) => {
  TPClient.logIt("DEBUG","Settings: New Settings from Touch-Portal")
  data.forEach( (setting) => {
    let key = Object.keys(setting)[0]
    pluginSettings[key] = setting[key]
    TPClient.logIt("DEBUG","Settings: Setting received for |"+key+"|")
  })
  if( prevCaptureInterval != pluginSettings[Constants.CAPTURE_INTERVAL_SETTING] ) {
    prevCaptureInterval = pluginSettings[Constants.CAPTURE_INTERVAL_SETTING]
  }
  buildHardwareList()
})

TPClient.on("Info", data => {
  TPClient.logIt("DEBUG","Info: received initial connect from Touch-Portal")
})

TPClient.on("Update", (curVersion,newVersion) => {
  TPClient.logIt("DEBUG","Update: there is an update curVersion:",curVersion,"newVersion:",newVersion)
  TPClient.sendNotification(`${pluginId}_update_notification_${newVersion}`,`Hardware Monitor Plugin Update Available`,
  `\nNew Version: ${newVersion}\n\nPlease updated to get the latest bug fixes and new features\n\nCurrent Installed Version: ${curVersion}`,
  [{id: `${pluginId}_update_notification_go_to_download`, title: "Go To Download Location" }]
);
});

TPClient.on("NotificationClicked", (data) => {
  if( data.optionId === `${pluginId}_update_notification_go_to_download`) {
    open(Constants.releaseUrl);
  }
});

TPClient.connect({ pluginId, updateUrl:Constants.updateUrl });