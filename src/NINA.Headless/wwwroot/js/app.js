function ninaApp() {
    return {
        tab: 'live',
        nightMode: false,

        // Live View
        exposure: 30,
        gain: 100,
        binning: '1',
        liveActive: false,
        looping: false,
        stats: {
            starCount: '--',
            hfr: null,
            mean: null
        },
        currentTime: '--:--:--',
        cameraTemp: null,

        // Mount
        mount: {
            ra: null,
            dec: null,
            alt: null,
            az: null,
            tracking: false
        },

        // Focus
        focusPosition: 0,
        focusStep: 50,

        // Sky
        skySearch: '',
        skyTarget: null,
        fov: {
            width: 2.82,
            height: 1.88
        },

        // Sequence
        sequence: [],

        // Settings
        settings: {
            indiHost: 'localhost',
            indiPort: 7624,
            latitude: 0,
            longitude: 0,
            altitude: 0,
            sensorWidth: 23.5,
            sensorHeight: 15.7,
            focalLength: 478
        },

        // Connection state
        indiConnected: false,
        statusWs: null,
        imageWs: null,

        init() {
            this.updateClock();
            setInterval(() => this.updateClock(), 1000);
            this.updateFov();

            const saved = localStorage.getItem('nina-settings');
            if (saved) {
                try {
                    const parsed = JSON.parse(saved);
                    Object.assign(this.settings, parsed);
                } catch (e) { /* ignore */ }
            }

            const nightSaved = localStorage.getItem('nina-night-mode');
            if (nightSaved === 'true') {
                this.nightMode = true;
                document.documentElement.setAttribute('data-theme', 'night');
            }

            this.$watch('settings', () => {
                this.updateFov();
                this.saveSettings();
            });
        },

        updateClock() {
            const now = new Date();
            this.currentTime = now.toLocaleTimeString('en-GB');
        },

        updateFov() {
            const sw = this.settings.sensorWidth;
            const sh = this.settings.sensorHeight;
            const fl = this.settings.focalLength;
            if (fl > 0) {
                this.fov.width = 2 * Math.atan(sw / (2 * fl)) * (180 / Math.PI);
                this.fov.height = 2 * Math.atan(sh / (2 * fl)) * (180 / Math.PI);
            }
        },

        saveSettings() {
            localStorage.setItem('nina-settings', JSON.stringify(this.settings));
        },

        toggleNightMode() {
            this.nightMode = !this.nightMode;
            document.documentElement.setAttribute('data-theme', this.nightMode ? 'night' : 'dark');
            localStorage.setItem('nina-night-mode', this.nightMode.toString());
        },

        // Camera
        async capture() {
            try {
                const resp = await fetch('/api/camera/capture', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        exposure: this.exposure,
                        gain: this.gain,
                        binning: parseInt(this.binning),
                        filter: null
                    })
                });
                if (resp.ok) {
                    this.liveActive = true;
                }
            } catch (e) {
                console.error('Capture failed:', e);
            }
        },

        async loopCapture() {
            this.looping = true;
            await this.capture();
        },

        stopCapture() {
            this.looping = false;
            this.liveActive = false;
        },

        // Mount
        async mountMove(direction) {
            try {
                if (direction === 'stop') {
                    return;
                }
                const dirMap = { n: [0, 1], s: [0, -1], e: [1, 0], w: [-1, 0] };
                const [raDelta, decDelta] = dirMap[direction] || [0, 0];
                console.log('Mount move:', direction, raDelta, decDelta);
            } catch (e) {
                console.error('Mount move failed:', e);
            }
        },

        async parkMount() {
            try {
                await fetch('/api/telescope/park', { method: 'POST' });
            } catch (e) {
                console.error('Park failed:', e);
            }
        },

        async unparkMount() {
            try {
                await fetch('/api/telescope/unpark', { method: 'POST' });
            } catch (e) {
                console.error('Unpark failed:', e);
            }
        },

        // Focus
        async focusMove(steps) {
            this.focusPosition += steps;
            console.log('Focus move to:', this.focusPosition);
        },

        async autoFocus() {
            console.log('Auto focus started');
        },

        // Sky
        async searchSky() {
            if (!this.skySearch.trim()) return;
            try {
                const resp = await fetch(`/api/sky/catalog/search?query=${encodeURIComponent(this.skySearch)}`);
                if (resp.ok) {
                    const data = await resp.json();
                    if (data.results && data.results.length > 0) {
                        this.skyTarget = data.results[0];
                    } else {
                        this.skyTarget = {
                            name: this.skySearch,
                            type: 'Unknown',
                            ra: '--',
                            dec: '--'
                        };
                    }
                }
            } catch (e) {
                console.error('Sky search failed:', e);
            }
        },

        async slewAndCenter() {
            if (!this.skyTarget) return;
            try {
                const resp = await fetch('/api/sky/slew-and-center', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        ra: parseFloat(this.skyTarget.ra) || 0,
                        dec: parseFloat(this.skyTarget.dec) || 0,
                        toleranceArcsec: 30
                    })
                });
                if (resp.ok) {
                    const data = await resp.json();
                    console.log('Slew job started:', data.jobId);
                }
            } catch (e) {
                console.error('Slew failed:', e);
            }
        },

        addToSequence() {
            if (!this.skyTarget) return;
            this.sequence.push({
                name: this.skyTarget.name,
                exposure: this.exposure,
                count: 10
            });
        },

        // Sequence
        async startSequence() {
            try {
                await fetch('/api/sequence/start', { method: 'POST' });
            } catch (e) {
                console.error('Start sequence failed:', e);
            }
        },

        async pauseSequence() {
            try {
                await fetch('/api/sequence/pause', { method: 'POST' });
            } catch (e) {
                console.error('Pause sequence failed:', e);
            }
        },

        async stopSequence() {
            try {
                await fetch('/api/sequence/stop', { method: 'POST' });
            } catch (e) {
                console.error('Stop sequence failed:', e);
            }
        },

        // INDI connection
        async connectIndi() {
            this.saveSettings();
            try {
                const resp = await fetch('/api/system/status');
                if (resp.ok) {
                    this.indiConnected = true;
                    this.connectStatusWs();
                }
            } catch (e) {
                this.indiConnected = false;
                console.error('INDI connection failed:', e);
            }
        },

        connectStatusWs() {
            if (this.statusWs) {
                this.statusWs.close();
            }
            const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
            this.statusWs = new WebSocket(`${protocol}//${location.host}/ws/status`);
            this.statusWs.onmessage = (evt) => {
                try {
                    const msg = JSON.parse(evt.data);
                    this.handleStatusMessage(msg);
                } catch (e) { /* ignore */ }
            };
            this.statusWs.onclose = () => {
                this.indiConnected = false;
            };
        },

        handleStatusMessage(msg) {
            if (msg.type === 'mount') {
                Object.assign(this.mount, msg.data);
            } else if (msg.type === 'camera') {
                if (msg.data.temperature !== undefined) {
                    this.cameraTemp = msg.data.temperature;
                }
            } else if (msg.type === 'stats') {
                Object.assign(this.stats, msg.data);
            }
        }
    };
}
