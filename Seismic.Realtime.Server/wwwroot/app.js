const processedByEl = document.getElementById("processed-by");
const observerEl = document.getElementById("observer");
const statusEl = document.getElementById("status");
const stationsEl = document.getElementById("stations");
const stationsEmpty = document.getElementById("stations-empty");
const alertsEl = document.getElementById("alerts");
const alertsEmpty = document.getElementById("alerts-empty");
const regionSelect = document.getElementById("region");
const applyRegionBtn = document.getElementById("apply-region");
const thresholdInput = document.getElementById("threshold");
const applyThresholdBtn = document.getElementById("apply-threshold");
const pingBtn = document.getElementById("ping");
const sendBadBtn = document.getElementById("send-bad");
const lastErrorEl = document.getElementById("last-error");
const subscriptionInfoEl = document.getElementById("subscription-info");
const stationTpl = document.getElementById("station-template");
const alertTpl = document.getElementById("alert-template");

const stations = new Map();   
let socket = null;
let reconnectTimer = null;


async function start() {
    try {
        const snapshot = await fetch("/monitoring/snapshot").then(r => r.json());
        processedByEl.textContent = snapshot.processedBy;
        thresholdInput.value = snapshot.threshold;
        renderInitialStations(snapshot.stations || []);
        renderInitialAlerts(snapshot.alerts || []);
    } catch (e) {
        console.warn("Снапшот не удалось получить:", e);
    }
    connect();
}

function connect() {
    const url = `${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/monitoring/dashboard-ws`;
    socket = new WebSocket(url);

    socket.onopen = () => {
        setStatus("online", "bg-emerald-100 text-emerald-800");
        showError(null);
    };

    socket.onmessage = event => {
        try {
            handleEvent(JSON.parse(event.data));
        } catch (e) {
            console.error("Ошибка разбора сообщения", e);
        }
    };

    socket.onclose = () => {
        setStatus("offline — переподключение", "bg-amber-100 text-amber-800");
        scheduleReconnect();
    };

    socket.onerror = () => {
      
    };
}

function scheduleReconnect() {
    if (reconnectTimer) return;
    reconnectTimer = setTimeout(async () => {
        reconnectTimer = null;
        await start();
    }, 2000);
}

function setStatus(text, classes) {
    statusEl.textContent = text;
    statusEl.className = "text-sm px-3 py-1 rounded-full " + classes;
}

function showError(message) {
    if (!message) {
        lastErrorEl.classList.add("hidden");
        lastErrorEl.textContent = "";
    } else {
        lastErrorEl.classList.remove("hidden");
        lastErrorEl.textContent = message;
    }
}

// --- обработка входящих событий ---

function handleEvent(evt) {
    switch (evt.type) {
        case "welcome":
            observerEl.textContent = `observer: ${evt.observerId.substring(0, 8)}`;
            processedByEl.textContent = evt.processedBy;
            thresholdInput.value = evt.threshold;
            stations.clear();
            stationsEl.innerHTML = "";
            renderInitialStations(evt.stations || []);
            alertsEl.innerHTML = "";
            renderInitialAlerts(evt.alerts || []);
            break;
        case "station-update":
            upsertStation(evt.station);
            break;
        case "station-offline":
        case "station-online":
            upsertStation(evt.station);
            break;
        case "alert":
            prependAlert(evt.alert);
            break;
        case "threshold-changed":
            thresholdInput.value = evt.threshold;
            break;
        case "subscription-changed":
            updateSubscription(evt.region, evt.subscribed);
            break;
        case "pong":
            flashPing();
            break;
        case "error":
            showError(`Ошибка сервера: ${evt.error}`);
            break;
    }
}

// --- отрисовка станций ---

function renderInitialStations(list) {
    for (const s of list) upsertStation(s);
    toggleEmpty();
}

function upsertStation(state) {
    if (!state) return;
    let card = stations.get(state.stationId);
    if (!card) {
        card = stationTpl.content.firstElementChild.cloneNode(true);
        stations.set(state.stationId, card);
        stationsEl.append(card);
    }
    card.querySelector("[data-station-id]").textContent = state.stationId;
    card.querySelector("[data-region]").textContent = `регион: ${state.region}`;
    card.querySelector("[data-amplitude]").textContent = `A = ${state.amplitude.toFixed(2)}`;
    card.querySelector("[data-noise]").textContent = `шум: ${state.noiseLevel.toFixed(2)}`;
    card.querySelector("[data-last-tick]").textContent =
        `последний тик: ${new Date(state.lastTickAt).toLocaleTimeString()}`;

    const statusEl = card.querySelector("[data-status]");
    statusEl.textContent = state.status;
    statusEl.className = "text-xs px-2 py-0.5 rounded-full " + statusClasses(state);

    toggleEmpty();
}

function statusClasses(state) {
    if (state.status === "offline") return "bg-slate-200 text-slate-600";
    if (state.status === "degraded") return "bg-amber-100 text-amber-800";
    if (state.amplitude >= parseFloat(thresholdInput.value || "0")) return "bg-red-100 text-red-800";
    return "bg-emerald-100 text-emerald-800";
}

function toggleEmpty() {
    stationsEmpty.classList.toggle("hidden", stations.size > 0);
}

// --- алерты ---

function renderInitialAlerts(list) {
    for (const a of list) prependAlert(a, false);
    toggleAlertsEmpty();
}

function prependAlert(alert, animate = true) {
    const node = alertTpl.content.firstElementChild.cloneNode(true);
    node.querySelector("[data-title]").textContent =
        `ТРЕВОГА: ${alert.stationId} (${alert.region}) · A = ${alert.amplitude.toFixed(2)}`;
    node.querySelector("[data-meta]").textContent =
        `порог ${alert.threshold.toFixed(2)} · ${new Date(alert.createdAt).toLocaleString()}`;
    alertsEl.prepend(node);
    toggleAlertsEmpty();
}

function toggleAlertsEmpty() {
    alertsEmpty.classList.toggle("hidden", alertsEl.children.length > 0);
}

// --- подписка/команды ---

function updateSubscription(region, subscribed) {
    if (region) {
        subscriptionInfoEl.textContent = `подписка: ${region}`;
    } else {
        subscriptionInfoEl.textContent = "подписка: все регионы";
    }
    showError(null);
}

function send(command) {
    if (!socket || socket.readyState !== WebSocket.OPEN) {
        showError("Нет соединения");
        return;
    }
    socket.send(typeof command === "string" ? command : JSON.stringify(command));
}

applyRegionBtn.onclick = () => {
    const region = regionSelect.value;
    if (region) {
        send({ type: "subscribe", region });
    } else {
        send({ type: "unsubscribe" });
    }
};

applyThresholdBtn.onclick = () => {
    const val = parseFloat(thresholdInput.value);
    if (isNaN(val) || val < 0) {
        showError("Порог должен быть неотрицательным числом");
        return;
    }
    send({ type: "set-threshold", threshold: val });
};

pingBtn.onclick = () => send({ type: "ping" });

sendBadBtn.onclick = () => {
    send("это не JSON");
};

function flashPing() {
    pingBtn.textContent = "pong!";
    setTimeout(() => (pingBtn.textContent = "ping"), 700);
}

start();
