// ============================================================================
// app.js — all of the chat page's behaviour (runs in the browser).
//
// Sections:
//   - Persistence:     save/load chats in the browser's localStorage
//   - Rendering:       draw the chat list and messages on the page
//   - Chat management: new/select/delete chats
//   - Sending:         POST to /api/chat and read the streamed reply
//   - Models:          fill the model dropdown from /api/models
//   - Wiring:          connect buttons and keys to the functions above
//
// The server keeps no state. The browser holds every conversation and sends
// the full history with each message.
// ============================================================================

// Strict mode turns some silent JavaScript mistakes into errors.
"use strict";

// localStorage key all chats are saved under.
const STORAGE_KEY = "ai-orchestrator.chats";

// Look up every element we need from index.html once, by its id="...".
const els = {
    sidebar: document.getElementById("sidebar"),
    sidebarToggle: document.getElementById("sidebarToggle"),
    newChat: document.getElementById("newChat"),
    chatList: document.getElementById("chatList"),
    modelSelect: document.getElementById("modelSelect"),
    thinkToggle: document.getElementById("thinkToggle"),
    toolsToggle: document.getElementById("toolsToggle"),
    writeToggle: document.getElementById("writeToggle"),
    writeToggleLabel: document.getElementById("writeToggleLabel"),
    folderHint: document.getElementById("folderHint"),
    chatTitle: document.getElementById("chatTitle"),
    status: document.getElementById("status"),
    messages: document.getElementById("messages"),
    emptyState: document.getElementById("emptyState"),
    composer: document.getElementById("composer"),
    input: document.getElementById("input"),
    sendBtn: document.getElementById("sendBtn"),
    stopBtn: document.getElementById("stopBtn"),
};

// ---------- app state ----------

// All conversations, newest first. Each looks like:
//   { id, title, messages: [{ role: "user"|"assistant", content, thinking?, tools?, meta? }] }
// (tools = the labels of the tools run for that reply, e.g. ["read_file: C:\\notes.txt"])
/** @type {{id:string,title:string,messages:{role:string,content:string,thinking?:string,tools?:string[]}[]}[]} */
let chats = loadChats();

// The chat on screen. null = a fresh chat, created when the first message is sent.
let activeChatId = chats[0]?.id ?? null;

// Set while a reply is being generated (null otherwise). Calling .abort() on it
// cancels the fetch, which the server sees as a disconnect, which stops Ollama.
// It also works as an "is busy?" flag throughout the code.
let abortController = null;

// ---------- persistence ----------

// localStorage only stores strings, so chats are saved as JSON text.
// Both functions use try/catch because localStorage can be missing or full
// (e.g. private browsing). The app still works, chats just don't survive a reload.
function loadChats() {
    try {
        return JSON.parse(localStorage.getItem(STORAGE_KEY)) ?? [];
    } catch {
        return [];
    }
}

function saveChats() {
    try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(chats));
    } catch {
        // Storage unavailable (private mode etc.) — chats just won't persist.
    }
}

function activeChat() {
    return chats.find(c => c.id === activeChatId) ?? null;
}

// ---------- rendering ----------

// Converts the model's Markdown (**bold**, ```code```, lists...) to HTML.
//   marked     — Markdown -> HTML (loaded from a CDN in index.html)
//   DOMPurify  — strips anything dangerous (e.g. <script>) from that HTML. Important:
//                the model's output is inserted as HTML, so without this a reply
//                containing a script tag could run code in the page.
// If the CDN scripts failed to load, show plain text instead.
function renderMarkdown(text) {
    if (window.marked && window.DOMPurify) {
        return DOMPurify.sanitize(marked.parse(text));
    }
    const div = document.createElement("div");
    div.textContent = text; // textContent escapes HTML, so this is safe
    return div.innerHTML.replace(/\n/g, "<br>");
}

// Redraws the sidebar list of chats.
function renderChatList() {
    els.chatList.innerHTML = "";
    for (const chat of chats) {
        const li = document.createElement("li");
        li.className = chat.id === activeChatId ? "active" : "";
        li.onclick = () => selectChat(chat.id);

        const title = document.createElement("span");
        title.className = "title";
        title.textContent = chat.title;

        const del = document.createElement("button");
        del.className = "delete";
        del.textContent = "✕";
        del.title = "Delete chat";
        del.onclick = e => {
            e.stopPropagation(); // don't also trigger the li's onclick (selecting the chat)
            deleteChat(chat.id);
        };

        li.append(title, del);
        els.chatList.append(li);
    }
}

// Redraws the whole message area for the active chat (used when switching chats).
// While a reply streams in, the page is updated piece by piece via createMessageView instead.
function renderMessages() {
    const chat = activeChat();
    els.messages.innerHTML = "";
    els.chatTitle.textContent = chat?.title ?? "New chat";

    if (!chat || chat.messages.length === 0) {
        els.messages.append(els.emptyState); // the "Chat with your local model" placeholder
        return;
    }
    for (const msg of chat.messages) {
        const view = createMessageView(msg.role);
        if (msg.role === "assistant") {
            view.setThinking(msg.thinking ?? "", false);
            (msg.tools ?? []).forEach(view.addTool);
            view.setContent(msg.content);
            if (msg.meta) view.setMeta(msg.meta);
        } else {
            view.setContent(msg.content);
        }
    }
    scrollToBottom(true);
}

/**
 * Adds one message bubble to the page and returns an object of functions for
 * updating it. While streaming, send() calls setContent() again and again as new
 * text arrives, and the bubble updates in place.
 *
 * Resulting HTML (assistant):
 *   <div class="msg assistant">
 *     <div class="bubble">
 *       <details class="thinking">...</details>   (only if the model thought)
 *       <div class="tool-calls">...</div>         (only if the model used tools)
 *       <div>answer...</div>
 *       <div class="meta">42 tokens · 12 tok/s</div>
 *     </div>
 *   </div>
 */
function createMessageView(role) {
    els.emptyState.remove();

    const wrap = document.createElement("div");
    wrap.className = `msg ${role}`; // "msg user" or "msg assistant" — styled in styles.css
    const bubble = document.createElement("div");
    bubble.className = "bubble";
    wrap.append(bubble);
    els.messages.append(wrap);

    // The thinking and tool blocks are created on first use, because many replies have neither.
    let thinkingEl = null;
    let thinkingTextEl = null;
    let toolsEl = null;
    const contentEl = document.createElement("div");
    bubble.append(contentEl);

    return {
        // live=true: block open with "Thinking…" (still thinking).
        // live=false: block collapsed as "Thought process" (thinking finished).
        setThinking(text, live) {
            if (!text) return;
            if (!thinkingEl) {
                // <details>/<summary> is a built-in HTML collapsible section — no JS needed to toggle it.
                thinkingEl = document.createElement("details");
                thinkingEl.className = "thinking";
                thinkingEl.innerHTML = "<summary></summary>";
                thinkingTextEl = document.createElement("div");
                thinkingTextEl.className = "thinking-text";
                thinkingEl.append(thinkingTextEl);
                bubble.prepend(thinkingEl); // put it above the answer
            }
            thinkingEl.open = live;
            thinkingEl.querySelector("summary").textContent = live ? "Thinking…" : "Thought process";
            thinkingTextEl.textContent = text;
        },
        // Adds a line for one tool run, e.g. "📂 list_directory: C:\Users\cbb".
        // Written as an arrow function so it still works when passed directly to forEach.
        addTool: label => {
            if (!toolsEl) {
                toolsEl = document.createElement("div");
                toolsEl.className = "tool-calls";
                bubble.insertBefore(toolsEl, contentEl); // above the answer, below any thinking
            }
            const line = document.createElement("div");
            line.className = "tool-call";
            const icons = {
                read_file: "📄", read_files: "📚", list_directory: "📂", search_files: "🔍",
                write_file: "✏️", edit_file: "🩹",
            };
            const icon = icons[label.split(":")[0]] ?? "🔧";
            line.textContent = `${icon} ${label}`;
            toolsEl.append(line);
        },
        // Replaces the bubble's text. streaming=true adds the blinking cursor (CSS class "cursor").
        setContent(text, streaming = false) {
            if (role === "user") {
                contentEl.textContent = text; // the user's text is shown exactly as typed
            } else {
                contentEl.innerHTML = renderMarkdown(text); // model replies are formatted as Markdown
            }
            contentEl.classList.toggle("cursor", streaming);
        },
        // The small grey stats line under a reply.
        setMeta(text) {
            let meta = bubble.querySelector(".meta");
            if (!meta) {
                meta = document.createElement("div");
                meta.className = "meta";
                bubble.append(meta);
            }
            meta.textContent = text;
        },
        setError(text) {
            wrap.classList.add("error"); // shown in red
            contentEl.classList.remove("cursor");
            contentEl.textContent = text;
        },
    };
}

// Keeps the newest text in view. It only auto-scrolls if the user is already near the
// bottom, so scrolling up to read older messages doesn't keep jumping back down.
// force=true always scrolls (e.g. right after sending a message).
function scrollToBottom(force = false) {
    const m = els.messages;
    const nearBottom = m.scrollHeight - m.scrollTop - m.clientHeight < 120;
    if (force || nearBottom) m.scrollTop = m.scrollHeight;
}

// Swaps Send <-> Stop and sets the status text in the top bar.
function setBusy(busy, statusText = "") {
    els.sendBtn.hidden = busy;
    els.stopBtn.hidden = !busy;
    els.status.textContent = statusText;
}

// ---------- chat management ----------

// A new chat isn't added to the list yet. That happens in send() when the first
// message goes out, so empty chats don't pile up in the sidebar.
function newChat() {
    activeChatId = null;
    renderChatList();
    renderMessages();
    els.input.focus();
}

function selectChat(id) {
    if (abortController) return; // don't switch mid-generation
    activeChatId = id;
    renderChatList();
    renderMessages();
}

function deleteChat(id) {
    if (abortController && id === activeChatId) return; // can't delete the chat that's generating
    chats = chats.filter(c => c.id !== id);
    if (activeChatId === id) activeChatId = chats[0]?.id ?? null;
    saveChats();
    renderChatList();
    renderMessages();
}

// ---------- sending ----------

// The main flow: called when the user submits a message.
//   1. Add the user's message to the chat (creating the chat if needed).
//   2. POST the whole conversation to /api/chat.
//   3. Read the streamed reply line by line and update the bubble live.
//   4. When finished (or stopped), save the reply to the chat.
async function send(text) {
    // 1. Get or create the chat. The first message (shortened) becomes the title.
    let chat = activeChat();
    if (!chat) {
        chat = {
            id: crypto.randomUUID(),
            title: text.length > 40 ? text.slice(0, 40) + "…" : text,
            messages: [],
        };
        chats.unshift(chat); // unshift = add to the front, so the newest chat is first
        activeChatId = chat.id;
        renderChatList();
        els.chatTitle.textContent = chat.title;
    }

    chat.messages.push({ role: "user", content: text });
    saveChats();
    createMessageView("user").setContent(text);

    // An empty assistant bubble with a blinking cursor, filled in as the reply streams.
    const view = createMessageView("assistant");
    view.setContent("", true);
    scrollToBottom(true);

    // The reply being built up. handleEvent() appends each new piece of text to it.
    const reply = { role: "assistant", content: "", thinking: "", tools: [] };
    abortController = new AbortController();
    setBusy(true, "Generating…");

    try {
        // 2. Send the conversation to our C# server (Program.cs -> POST /api/chat).
        const response = await fetch("/api/chat", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                model: els.modelSelect.value || null,
                think: els.thinkToggle.checked,
                useTools: els.toolsToggle.checked,
                allowWrite: els.toolsToggle.checked && els.writeToggle.checked,
                // Send only role/content. The stored thinking and stats are for display only;
                // sending them back would waste the model's context.
                messages: chat.messages.map(m => ({ role: m.role, content: m.content })),
            }),
            signal: abortController.signal, // lets the Stop button cancel this request
        });
        if (!response.ok || !response.body) {
            throw new Error(`Server returned ${response.status}`);
        }

        // 3. Read the streamed body piece by piece.
        // response.body is a stream. reader.read() returns the next block of bytes as it
        // arrives. TextDecoder turns bytes into text ({ stream: true } handles a multi-byte
        // character such as "æ" that is split across two blocks).
        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = "";

        while (true) {
            const { value, done } = await reader.read();
            if (done) break; // the server closed the stream: the reply is complete
            buffer += decoder.decode(value, { stream: true });

            // A block of bytes doesn't line up with lines. It might hold half a line or
            // several lines. So add it to a buffer, take out each complete line (ending in
            // "\n"), and keep any unfinished remainder for the next block.
            let newline;
            while ((newline = buffer.indexOf("\n")) >= 0) {
                const line = buffer.slice(0, newline).trim();
                buffer = buffer.slice(newline + 1);
                if (line) handleEvent(JSON.parse(line), reply, view);
            }
        }
    } catch (err) {
        // AbortError means the user clicked Stop. That's not an error, so show nothing.
        if (err.name !== "AbortError") {
            reply.error = err.message;
            view.setError(`Error: ${err.message}`);
        }
    } finally {
        // 4. Runs whether the reply finished, failed or was stopped.
        abortController = null;
        if (!reply.error) {
            view.setThinking(reply.thinking, false); // collapse the thinking block
            view.setContent(reply.content || "_(no response)_", false); // remove the cursor
        }
        // Save the reply (even a partial one after Stop) so it's part of the history
        // for the next message.
        if (reply.content) {
            delete reply.error;
            chat.messages.push(reply);
        }
        saveChats();
        setBusy(false);
        els.input.focus();
    }
}

// Handles one streamed event from the server (the ChatStreamEvent type in OllamaModels.cs).
function handleEvent(evt, reply, view) {
    switch (evt.type) {
        case "thinking":
            reply.thinking += evt.text;
            view.setThinking(reply.thinking, true);
            els.status.textContent = "Thinking…";
            break;
        case "content":
            // The first piece of the answer means thinking has finished, so collapse that block.
            if (!reply.content && reply.thinking) view.setThinking(reply.thinking, false);
            reply.content += evt.text;
            // Re-render the whole reply so far as Markdown. Markdown needs the full text to
            // format correctly (e.g. an opening ``` only makes sense once its closing ``` arrives).
            view.setContent(reply.content, true);
            els.status.textContent = "Generating…";
            break;
        case "tool":
            // The server is running a tool for the model (e.g. reading a file).
            // After this the model gets another turn, so more thinking/content follows.
            reply.tools.push(evt.text);
            view.addTool(evt.text);
            els.status.textContent = "Using tools…";
            break;
        case "done":
            if (evt.tokens) {
                // ?. and ?? guard against tokensPerSecond being missing.
                reply.meta = `${evt.tokens} tokens · ${evt.tokensPerSecond?.toFixed(1) ?? "?"} tok/s`;
                view.setMeta(reply.meta);
            }
            break;
        case "error":
            reply.error = evt.text;
            view.setError(`Error: ${evt.text}`);
            break;
    }
    scrollToBottom();
}

// ---------- models ----------

// Fills the model dropdown. Promise.all makes both requests at the same time
// and waits for both to finish.
async function loadModels() {
    try {
        const [config, models] = await Promise.all([
            fetch("/api/config").then(r => r.json()),
            fetch("/api/models").then(r => {
                if (!r.ok) throw new Error("Ollama unreachable"); // the server returned 502
                return r.json();
            }),
        ]);
        els.folderHint.textContent = config.allowedFolders?.length
            ? `Can read: ${config.allowedFolders.join(", ")}`
            : "No folders configured (see appsettings.json)";

        // Writing switched off on the server: hide the toggle so it doesn't look available.
        if (!config.allowWrite) {
            els.writeToggle.checked = false;
            els.writeToggleLabel.hidden = true;
        }

        els.modelSelect.innerHTML = "";
        for (const m of models) {
            const opt = document.createElement("option");
            opt.value = m.name;
            opt.textContent = m.name;
            opt.selected = m.name === config.defaultModel; // preselect the default from appsettings.json
            els.modelSelect.append(opt);
        }
        if (models.length === 0) {
            els.status.textContent = "No models installed in Ollama";
        }
    } catch (err) {
        els.status.textContent = `⚠ ${err.message}`;
    }
}

// ---------- wiring (event listeners) ----------

// The form's "submit" event fires from the Send button or from the Enter key (below).
els.composer.addEventListener("submit", e => {
    e.preventDefault(); // stop the browser's default form behaviour (reloading the page)
    const text = els.input.value.trim();
    if (!text || abortController) return; // ignore empty input, or input while generating
    els.input.value = "";
    autoResize();
    send(text);
});

// Enter sends, Shift+Enter adds a new line.
// isComposing: true while typing with an input method (e.g. Chinese/Japanese
// keyboards), where Enter confirms a character rather than sending the message.
els.input.addEventListener("keydown", e => {
    if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
        e.preventDefault();
        els.composer.requestSubmit();
    }
});

// Grow the textarea with its content, up to 200px (then it scrolls).
function autoResize() {
    els.input.style.height = "auto";
    els.input.style.height = Math.min(els.input.scrollHeight, 200) + "px";
}
els.input.addEventListener("input", autoResize);

// ?. = do nothing if abortController is null (nothing is generating).
els.stopBtn.addEventListener("click", () => abortController?.abort());
els.newChat.addEventListener("click", newChat);
els.sidebarToggle.addEventListener("click", () => els.sidebar.classList.toggle("collapsed"));
// Writing needs file access, so grey out the write toggle while file access is off.
els.toolsToggle.addEventListener("change", () => (els.writeToggle.disabled = !els.toolsToggle.checked));

// ---------- startup ----------
renderChatList();
renderMessages();
loadModels();
els.input.focus();
