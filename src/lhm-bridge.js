const { spawn } = require('child_process')
const path = require('path')
const net = require('net')

class LHMBridge {
  constructor(options = {}) {
    this.exePath = options.exePath || this._getDefaultExePath()
    this.process = null
    this.socket = null
    this.pendingRequests = new Map()
    this.requestId = 0
    this.isReady = false
    this.restartAttempts = 0
    this.maxRestartAttempts = options.maxRestartAttempts || 5
    this.restartDelay = options.restartDelay || 1000
    this.commandTimeout = options.commandTimeout || 10000
    this.onError = options.onError || (() => {})
    this.onReady = options.onReady || (() => {})
    this.onExit = options.onExit || (() => {})
    this.port = null
    this.buffer = ''
  }

  _getDefaultExePath() {
    // When running from pkg, __dirname points to snapshot
    // LHMBridge.exe should be in the same directory as the main executable
    const isPackaged = typeof process.pkg !== 'undefined'
    if (isPackaged) {
      return path.join(path.dirname(process.execPath), 'LHMBridge.exe')
    }
    // Development mode
    return path.join(__dirname, '..', 'csharp', 'LHMBridge', 'bin', 'Release', 'net10.0', 'win-x64', 'publish', 'LHMBridge.exe')
  }

  _getAvailablePort() {
    return new Promise((resolve, reject) => {
      const server = net.createServer()
      server.listen(0, '127.0.0.1', () => {
        const port = server.address().port
        server.close(() => resolve(port))
      })
      server.on('error', reject)
    })
  }

  async start() {
    try {
      // Get an available port
      this.port = await this._getAvailablePort()

      // Spawn LHMBridge.exe with elevation using PowerShell
      await this._spawnElevated()

      // Connect via TCP with retries
      await this._connectWithRetry()

      this.isReady = true
      this.restartAttempts = 0
      this.onReady()
    } catch (err) {
      this.onError(err)
      throw err
    }
  }

  _spawnElevated() {
    return new Promise((resolve, reject) => {
      // Use PowerShell to start the process with elevation
      // -WindowStyle Hidden hides the console window
      const psCommand = `Start-Process -FilePath '${this.exePath}' -ArgumentList '--port','${this.port}' -Verb RunAs -WindowStyle Hidden`

      this.process = spawn('powershell', ['-Command', psCommand], {
        shell: false,
        stdio: ['ignore', 'pipe', 'pipe']
      })

      this.process.on('error', (err) => {
        reject(err)
      })

      // PowerShell process exits quickly after launching the elevated process
      this.process.on('exit', (code) => {
        if (code !== 0) {
          reject(new Error(`PowerShell exited with code ${code} - UAC may have been cancelled`))
        } else {
          // PowerShell launched successfully, elevated process should be starting
          resolve()
        }
      })

      // Capture any PowerShell errors
      let stderrData = ''
      this.process.stderr.on('data', (data) => {
        stderrData += data.toString()
      })

      // Give a short timeout for PowerShell to complete
      setTimeout(() => {
        if (stderrData) {
          reject(new Error(`PowerShell error: ${stderrData}`))
        }
      }, 5000)
    })
  }

  _connectWithRetry(maxRetries = 20, delay = 250) {
    return new Promise((resolve, reject) => {
      let attempts = 0

      const tryConnect = () => {
        attempts++

        this.socket = new net.Socket()

        this.socket.connect(this.port, '127.0.0.1', () => {
          // Connected successfully
          this._setupSocketHandlers()
          resolve()
        })

        this.socket.on('error', (err) => {
          this.socket.destroy()

          if (attempts < maxRetries) {
            setTimeout(tryConnect, delay)
          } else {
            reject(new Error(`Failed to connect to LHMBridge after ${maxRetries} attempts: ${err.message}`))
          }
        })
      }

      tryConnect()
    })
  }

  _setupSocketHandlers() {
    this.socket.on('data', (data) => {
      this.buffer += data.toString()

      // Process complete lines
      let newlineIndex
      while ((newlineIndex = this.buffer.indexOf('\n')) !== -1) {
        const line = this.buffer.substring(0, newlineIndex).trim()
        this.buffer = this.buffer.substring(newlineIndex + 1)

        if (line) {
          this._handleResponse(line)
        }
      }
    })

    this.socket.on('error', (err) => {
      this.isReady = false
      this.onError(err)
      this._rejectAllPending(err)
    })

    this.socket.on('close', () => {
      this.isReady = false
      this.onExit(0, null)
      this._rejectAllPending(new Error('Connection closed'))

      // Attempt restart if not intentional shutdown
      if (this.restartAttempts < this.maxRestartAttempts) {
        this._scheduleRestart()
      }
    })
  }

  _scheduleRestart() {
    this.restartAttempts++
    const delay = this.restartDelay * Math.pow(2, this.restartAttempts - 1)
    setTimeout(() => {
      this.start().catch((err) => {
        this.onError(new Error(`Restart attempt ${this.restartAttempts} failed: ${err.message}`))
      })
    }, delay)
  }

  _handleResponse(line) {
    try {
      const response = JSON.parse(line)

      // Find the oldest pending request and resolve it
      // Since we send commands sequentially, FIFO order is maintained
      if (this.pendingRequests.size > 0) {
        const [requestId, handler] = this.pendingRequests.entries().next().value
        this.pendingRequests.delete(requestId)
        clearTimeout(handler.timeout)

        if (response.success) {
          handler.resolve(response.data)
        } else {
          handler.reject(new Error(response.error || 'Unknown error'))
        }
      }
    } catch (e) {
      // Not valid JSON, ignore
    }
  }

  _rejectAllPending(error) {
    for (const [, handler] of this.pendingRequests) {
      clearTimeout(handler.timeout)
      handler.reject(error)
    }
    this.pendingRequests.clear()
  }

  async _sendCommand(command) {
    if (!this.isReady || !this.socket) {
      throw new Error('LHMBridge is not ready')
    }

    return new Promise((resolve, reject) => {
      const requestId = ++this.requestId

      const timeout = setTimeout(() => {
        this.pendingRequests.delete(requestId)
        reject(new Error(`Command '${command}' timed out`))
      }, this.commandTimeout)

      this.pendingRequests.set(requestId, { resolve, reject, timeout })

      const commandJson = JSON.stringify({ command }) + '\n'
      this.socket.write(commandJson)
    })
  }

  async getHardware() {
    return this._sendCommand('getHardware')
  }

  async getSensors() {
    return this._sendCommand('getSensors')
  }

  async stop() {
    this.maxRestartAttempts = 0 // Prevent auto-restart

    try {
      if (this.isReady && this.socket) {
        await this._sendCommand('shutdown')
      }
    } catch (e) {
      // Ignore errors during shutdown
    }

    if (this.socket) {
      this.socket.destroy()
      this.socket = null
    }

    this.isReady = false
    this._rejectAllPending(new Error('Bridge stopped'))
    this.process = null
  }
}

module.exports = LHMBridge
