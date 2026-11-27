const novramKey = "starwars.x2212";
const keys = new Map([
    ["ArrowLeft", 1], ["ArrowRight", 2], ["ArrowUp", 4], ["ArrowDown", 8],
    ["KeyC", 16], ["KeyV", 32], ["Space", 192], ["ShiftRight", 128],
    ["Digit1", 256], ["Digit2", 512], ["KeyA", 1024], ["KeyT", 2048], ["F2", 4096]
]);

let canvas;
let beam;
let fieldWidth;
let fieldHeight;
let latestVectors;
let vectorLength = 0;
const beamColors = new Map();
let audioContext;
let audioNode;
let audioStats;

export function initialize(cabinet, width, height, pcmRate) {
    canvas = document.querySelector("#vectors");
    beam = canvas.getContext("2d", { alpha: false });
    fieldWidth = width;
    fieldHeight = height;
    new ResizeObserver(paintVectors).observe(canvas);

    const pauseButton = document.querySelector("#pause-game");
    const muteButton = document.querySelector("#mute-game");
    const message = document.querySelector("#browser-message");
    const diagnosticsPanel = document.querySelector("#game-diagnostics");
    const diagnostics = document.querySelector("#diagnostics");
    const pressedKeys = new Set();
    const touchButtons = new Map();
    let paused = false;
    let muted = false;
    let audioStarting = false;
    let lastFrame;
    let lastAudioTime;
    let measurementStart;
    let frameCount = 0;
    let frameWork = 0;
    let generated = 0;
    let savedImage = loadNovram();
    let mouseButtons = 0;
    let touchYoke;
    let yaw = -1;
    let pitch = -1;

    function showMessage(text) {
        message.textContent = text;
        message.hidden = !text;
    }

    function saveNovram() {
        const image = cabinet.invokeMethod("CaptureNovram");
        if (image === savedImage) return;
        try {
            localStorage.setItem(novramKey, image);
            savedImage = image;
        } catch {
            // The cabinet can still run when the browser blocks local storage.
        }
    }

    function releaseControls() {
        pressedKeys.clear();
        touchButtons.clear();
        touchYoke = undefined;
        mouseButtons = 0;
        yaw = -1;
    }

    function resetTiming() {
        lastFrame = undefined;
        lastAudioTime = undefined;
        measurementStart = undefined;
        audioNode?.port.postMessage("clear");
    }

    function setYoke(event) {
        const bounds = canvas.getBoundingClientRect();
        const x = (event.clientX - bounds.left) / bounds.width;
        const y = (event.clientY - bounds.top) / bounds.height;
        yaw = Math.round(Math.min(1, Math.max(0, 0.5 + 1.5 * (x - 0.5))) * 255);
        pitch = Math.round(Math.min(1, Math.max(0, 0.5 - 1.5 * (y - 0.5))) * 255);
    }

    function frame(now) {
        if (paused || document.hidden) {
            requestAnimationFrame(frame);
            return;
        }

        const audioRunning = audioNode !== undefined && audioContext?.state === "running";
        const audioTime = audioRunning ? audioContext.currentTime : undefined;
        if (lastFrame === undefined || now - lastFrame > 100) {
            resetTiming();
            lastFrame = now;
            lastAudioTime = audioTime;
            requestAnimationFrame(frame);
            return;
        }

        const elapsed = audioTime !== undefined && lastAudioTime !== undefined
            ? audioTime - lastAudioTime : (now - lastFrame) / 1000;
        lastFrame = now;
        lastAudioTime = audioTime;

        if (elapsed > 0.1) {
            resetTiming();
            requestAnimationFrame(frame);
            return;
        }

        const workStarted = performance.now();
        let controls = mouseButtons;
        for (const code of pressedKeys) controls |= keys.get(code);
        for (const buttons of touchButtons.values()) controls |= buttons;

        try {
            generated += cabinet.invokeMethod("AdvanceFrame", elapsed, controls, yaw, pitch, audioRunning && !muted);
        } catch (error) {
            resetTiming();
            showMessage(`Emulation stopped. ${error.message}`);
            console.error(error);
            return;
        }

        frameCount++;
        frameWork += performance.now() - workStarted;
        if (measurementStart === undefined) {
            measurementStart = now;
            frameCount = 0;
            frameWork = 0;
            generated = 0;
        } else if (now - measurementStart >= 1000) {
            const seconds = (now - measurementStart) / 1000;
            const audio = audioStats && audioRunning && !muted
                ? `${(audioStats.queued * 1000 / pcmRate).toFixed(0)} ms buffered · ${audioStats.underruns} underruns · ${audioStats.overflows} overflows · peak ${audioStats.peak.toFixed(3)}`
                : muted ? "Muted" : (audioContext?.state ?? "Sound off");
            diagnostics.textContent = `${Math.round(frameCount / seconds)} fps · ${(frameWork / frameCount).toFixed(1)} ms per frame · ${Math.round(generated / seconds)} samples/s · ${audio}`;
            diagnosticsPanel.hidden = false;
            saveNovram();
            measurementStart = now;
            frameCount = 0;
            frameWork = 0;
            generated = 0;
        }

        requestAnimationFrame(frame);
    }

    async function enableAudio() {
        if (audioStarting || audioNode) return;
        audioStarting = true;
        try {
            await openAudio(pcmRate);
            muteButton.textContent = "Mute";
            showMessage("");
        } catch (error) {
            showMessage(`Audio unavailable. ${error.message}`);
        } finally {
            audioStarting = false;
        }
    }

    function togglePause() {
        paused = !paused;
        pauseButton.textContent = paused ? "Resume" : "Pause";
        pauseButton.setAttribute("aria-pressed", String(paused));
        document.querySelector("#paused").hidden = !paused;
        releaseControls();
        resetTiming();
        if (paused) {
            saveNovram();
        } else if (audioContext && !muted) {
            audioContext.resume().catch(error => showMessage(`Audio unavailable. ${error.message}`));
        }
        canvas.focus();
    }

    function toggleMute() {
        if (!audioNode) {
            void enableAudio();
            return;
        }
        muted = !muted;
        muteButton.textContent = muted ? "Unmute" : "Mute";
        muteButton.setAttribute("aria-pressed", String(muted));
        audioNode.port.postMessage("clear");
        if (!muted) {
            audioContext.resume().catch(error => showMessage(`Audio unavailable. ${error.message}`));
        }
        canvas.focus();
    }

    pauseButton.addEventListener("click", togglePause);
    muteButton.addEventListener("click", toggleMute);
    window.addEventListener("keydown", event => {
        if (event.code === "KeyP" && !event.repeat) {
            event.preventDefault();
            togglePause();
        } else if (event.code === "F1" && !event.repeat) {
            event.preventDefault();
            cabinet.invokeMethod("ToggleSelfTest");
        } else if (keys.has(event.code) && !event.metaKey && !event.ctrlKey && !event.altKey) {
            if (event.target instanceof HTMLButtonElement && event.code === "Space") return;
            event.preventDefault();
            void enableAudio();
            pressedKeys.add(event.code);
            if (keys.get(event.code) < 16) yaw = -1;
        }
    });
    window.addEventListener("keyup", event => pressedKeys.delete(event.code));
    window.addEventListener("blur", releaseControls);
    window.addEventListener("pagehide", saveNovram);
    document.addEventListener("visibilitychange", () => {
        releaseControls();
        resetTiming();
        if (document.hidden) saveNovram();
    });

    canvas.addEventListener("contextmenu", event => event.preventDefault());
    canvas.addEventListener("pointerdown", event => {
        if (paused) return;
        if (event.pointerType !== "mouse" && touchYoke !== undefined) return;
        event.preventDefault();
        canvas.focus();
        canvas.setPointerCapture(event.pointerId);
        setYoke(event);
        if (event.pointerType === "mouse") {
            mouseButtons = (event.buttons & 1 ? 64 : 0) | (event.buttons & 2 ? 128 : 0);
        } else {
            touchYoke = event.pointerId;
        }
    });
    canvas.addEventListener("pointerup", enableAudio);
    canvas.addEventListener("pointermove", event => {
        if (paused) return;
        if (event.pointerType === "mouse") {
            mouseButtons = (event.buttons & 1 ? 64 : 0) | (event.buttons & 2 ? 128 : 0);
        } else if (touchYoke !== event.pointerId) {
            return;
        }
        setYoke(event);
    });
    canvas.addEventListener("pointerleave", event => {
        if (event.pointerType === "mouse" && !canvas.hasPointerCapture(event.pointerId)) yaw = -1;
    });
    canvas.addEventListener("lostpointercapture", event => {
        if (event.pointerType === "mouse") mouseButtons = 0;
        if (touchYoke === event.pointerId) {
            touchYoke = undefined;
            yaw = -1;
        }
    });
    for (const button of document.querySelectorAll("#touch-controls button")) {
        button.addEventListener("pointerdown", event => {
            if (paused) return;
            event.preventDefault();
            button.setPointerCapture(event.pointerId);
            touchButtons.set(event.pointerId, Number(button.dataset.controls));
        });
        button.addEventListener("pointerup", enableAudio);
        button.addEventListener("lostpointercapture", event => touchButtons.delete(event.pointerId));
    }

    requestAnimationFrame(frame);
}

async function openAudio(inputRate) {
    if (!window.isSecureContext) {
        throw new Error("Open the game over HTTPS or localhost to enable sound.");
    }
    if (!window.AudioContext) {
        throw new Error("This browser does not support Web Audio.");
    }

    audioContext = new AudioContext({ latencyHint: "interactive" });
    try {
        const resumed = audioContext.resume();
        await Promise.all([
            resumed,
            audioContext.audioWorklet.addModule(new URL("./pcm-worklet.js", import.meta.url))
        ]);
        audioNode = new AudioWorkletNode(audioContext, "starwars-pcm", {
            numberOfInputs: 0,
            numberOfOutputs: 1,
            outputChannelCount: [1],
            processorOptions: { inputRate }
        });
        audioNode.port.onmessage = event => { audioStats = event.data; };
        audioNode.connect(audioContext.destination);
    } catch (error) {
        await audioContext.close();
        audioContext = undefined;
        audioNode = undefined;
        throw error;
    }
}

export function queueAudio(bytes) {
    const copy = bytes.slice();
    audioNode.port.postMessage(new Float32Array(copy.buffer), [copy.buffer]);
}

export function loadNovram() {
    try {
        return localStorage.getItem(novramKey);
    } catch {
        return null;
    }
}

export function drawVectors(bytes, count) {
    vectorLength = count * 5;
    const data = new Int32Array(bytes.buffer, bytes.byteOffset, vectorLength);
    if (!latestVectors || latestVectors.length < vectorLength) {
        latestVectors = new Int32Array(Math.max(vectorLength, (latestVectors?.length ?? 0) * 2));
    }
    latestVectors.set(data);
    paintVectors();
}

function paintVectors() {
    const width = Math.max(1, Math.round(canvas.clientWidth * devicePixelRatio));
    const height = Math.max(1, Math.round(canvas.clientHeight * devicePixelRatio));
    if (canvas.width !== width || canvas.height !== height) {
        canvas.width = width;
        canvas.height = height;
    }

    beam.fillStyle = "black";
    beam.fillRect(0, 0, width, height);
    if (!latestVectors) return;

    const scaleX = width / (fieldWidth * 65536);
    const scaleY = height / (fieldHeight * 65536);
    beam.lineWidth = Math.max(1, height / 540);
    beam.lineCap = "round";

    let previousColor = -1;
    for (let i = 0; i < vectorLength; i += 5) {
        const color = latestVectors[i + 4] >>> 0;
        if (color !== previousColor) {
            if (i !== 0) beam.stroke();
            let cssColor = beamColors.get(color);
            if (cssColor === undefined) {
                const brightness = Math.sqrt((color >>> 24) / 255);
                const red = Math.round(((color >>> 16) & 255) * brightness);
                const green = Math.round(((color >>> 8) & 255) * brightness);
                const blue = Math.round((color & 255) * brightness);
                cssColor = `rgb(${red}, ${green}, ${blue})`;
                beamColors.set(color, cssColor);
            }
            beam.strokeStyle = cssColor;
            beam.beginPath();
            previousColor = color;
        }
        beam.moveTo(latestVectors[i] * scaleX, latestVectors[i + 1] * scaleY);
        beam.lineTo(latestVectors[i + 2] * scaleX, latestVectors[i + 3] * scaleY);
    }
    if (vectorLength !== 0) beam.stroke();
}
