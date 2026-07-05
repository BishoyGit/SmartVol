"""
Per-application + global volume control with an on-screen overlay.

- Up / Down arrows    -> raise / lower the volume of whichever app
                         currently has focus (the foreground window).
- Left / Right arrows -> raise / lower the global (system/master)
                         volume.

An overlay pops up showing the app's icon, name, and current volume
level (or "System Volume" for the global control).

--------------------------------------------------------------------
INSTALL (run once, in an elevated or normal cmd/PowerShell):

    pip install pycaw comtypes psutil keyboard pywin32 pillow

RUN:

    python app_volume_overlay.py

NOTES / CAVEATS:
- This script GLOBALLY captures all four arrow keys (with
  suppress=True), meaning their normal behavior (scrolling, cursor
  movement, game controls, etc.) will stop working system-wide while
  this script runs. If you don't want that, change the HOTKEY_* values
  below to something like "ctrl+up" / "ctrl+down" / "ctrl+left" / "ctrl+right".
- Apps running as Administrator (e.g. some games) may not be
  reachable by the keyboard hook unless you also run this script as
  Administrator.
- Not every foreground window has an audio session (e.g. it hasn't
  played sound yet, or it's a system window) — the overlay will say
  "No audio session" in that case.
- Close the script from the terminal (Ctrl+C) or Task Manager to stop it.
--------------------------------------------------------------------
"""

import queue
import threading
import time
import tkinter as tk
from tkinter import ttk

import keyboard
import psutil
import win32api
import win32con
import win32gui
import win32process
import win32ui
from PIL import Image, ImageTk
from pycaw.pycaw import AudioUtilities, IAudioEndpointVolume
from comtypes import CLSCTX_ALL

# ----------------------------- CONFIG -------------------------------
STEP = 0.05                 # volume change per key press (5%)
HOTKEY_UP = "up"            # per-app volume up   (change to "ctrl+up" etc. if desired)
HOTKEY_DOWN = "down"        # per-app volume down
HOTKEY_LEFT = "left"        # global/system volume down
HOTKEY_RIGHT = "right"      # global/system volume up
SUPPRESS_KEYS = True         # block the normal OS action for these keys
OVERLAY_DURATION_MS = 1300   # how long the overlay stays visible
OVERLAY_WIDTH = 340
OVERLAY_HEIGHT = 100
OVERLAY_OPACITY = 0.92
ICON_SIZE = 40
# ---------------------------------------------------------------------

event_queue = queue.Queue()
_icon_cache = {}


# ----------------------- Windows helper functions ---------------------

def get_foreground_pid_and_title():
    hwnd = win32gui.GetForegroundWindow()
    if not hwnd:
        return None, None
    try:
        _, pid = win32process.GetWindowThreadProcessId(hwnd)
        title = win32gui.GetWindowText(hwnd)
        return pid, title
    except Exception:
        return None, None


def get_process_name(pid):
    try:
        return psutil.Process(pid).name()
    except Exception:
        return None


def get_exe_path(pid):
    try:
        return psutil.Process(pid).exe()
    except Exception:
        return None


def find_audio_session(pid, proc_name):
    """Find the audio session matching this pid, falling back to
    matching by process name (covers apps like browsers where the
    audio-producing subprocess differs from the focused window's pid)."""
    sessions = AudioUtilities.GetAllSessions()

    # Pass 1: exact pid match
    for session in sessions:
        proc = session.Process
        if proc and proc.pid == pid:
            return session

    # Pass 2: same executable name, any pid
    if proc_name:
        for session in sessions:
            proc = session.Process
            if proc:
                try:
                    if proc.name().lower() == proc_name.lower():
                        return session
                except Exception:
                    continue
    return None


def get_master_volume_interface():
    """Return an IAudioEndpointVolume interface for the default output
    device, used to control the global/system volume."""
    devices = AudioUtilities.GetSpeakers()
    interface = devices.Activate(IAudioEndpointVolume._iid_, CLSCTX_ALL, None)
    return interface.QueryInterface(IAudioEndpointVolume)


def get_icon_image(exe_path, size=ICON_SIZE):
    """Extract an app's .exe icon as a PIL Image (RGBA)."""
    if not exe_path:
        return None
    if exe_path in _icon_cache:
        return _icon_cache[exe_path]

    img = None
    try:
        large, small = win32gui.ExtractIconEx(exe_path, 0)
        hicon = None
        if small:
            hicon = small[0]
        elif large:
            hicon = large[0]

        if hicon:
            ico_x = win32api.GetSystemMetrics(win32con.SM_CXICON)
            ico_y = win32api.GetSystemMetrics(win32con.SM_CYICON)

            hdc = win32ui.CreateDCFromHandle(win32gui.GetDC(0))
            hbmp = win32ui.CreateBitmap()
            hbmp.CreateCompatibleBitmap(hdc, ico_x, ico_y)
            hdc_mem = hdc.CreateCompatibleDC()
            hdc_mem.SelectObject(hbmp)
            hdc_mem.DrawIcon((0, 0), hicon)

            bmpinfo = hbmp.GetInfo()
            bmpstr = hbmp.GetBitmapBits(True)
            raw = Image.frombuffer(
                "RGBA",
                (bmpinfo["bmWidth"], bmpinfo["bmHeight"]),
                bmpstr, "raw", "BGRA", 0, 1,
            )
            img = raw.resize((size, size), Image.LANCZOS)

        # cleanup handles
        for h in (large or []):
            win32gui.DestroyIcon(h)
        for h in (small or []):
            win32gui.DestroyIcon(h)
    except Exception:
        img = None

    _icon_cache[exe_path] = img
    return img


# ----------------------------- Overlay UI -----------------------------

class Overlay:
    def __init__(self, root):
        self.root = root
        self.hide_job = None

        self.win = tk.Toplevel(root)
        self.win.overrideredirect(True)
        self.win.attributes("-topmost", True)
        self.win.attributes("-alpha", OVERLAY_OPACITY)
        self.win.configure(bg="#1e1e1e")
        self.win.withdraw()

        self._place_window()

        frame = tk.Frame(self.win, bg="#1e1e1e")
        frame.pack(fill="both", expand=True, padx=14, pady=12)

        self.icon_label = tk.Label(frame, bg="#1e1e1e")
        self.icon_label.grid(row=0, column=0, rowspan=2, padx=(0, 12))

        self.name_label = tk.Label(
            frame, text="", fg="white", bg="#1e1e1e",
            font=("Segoe UI", 11, "bold"), anchor="w",
        )
        self.name_label.grid(row=0, column=1, sticky="w")

        self.pct_label = tk.Label(
            frame, text="", fg="#cccccc", bg="#1e1e1e",
            font=("Segoe UI", 10), anchor="w",
        )
        self.pct_label.grid(row=1, column=1, sticky="w")

        style = ttk.Style()
        style.theme_use("default")
        style.configure(
            "Overlay.Horizontal.TProgressbar",
            troughcolor="#3a3a3a", background="#4caf50",
            thickness=8, borderwidth=0,
        )
        self.progress = ttk.Progressbar(
            frame, style="Overlay.Horizontal.TProgressbar",
            orient="horizontal", length=OVERLAY_WIDTH - 90,
            mode="determinate", maximum=100,
        )
        self.progress.grid(row=2, column=0, columnspan=2, sticky="we", pady=(8, 0))

        self._tk_icon_ref = None  # keep a reference so it isn't GC'd

    def _place_window(self):
        screen_w = self.win.winfo_screenwidth()
        screen_h = self.win.winfo_screenheight()
        x = (screen_w - OVERLAY_WIDTH) // 2
        y = screen_h - OVERLAY_HEIGHT - 140
        self.win.geometry(f"{OVERLAY_WIDTH}x{OVERLAY_HEIGHT}+{x}+{y}")

    def show(self, name, pct, pil_icon, no_session=False):
        if pil_icon is not None:
            self._tk_icon_ref = ImageTk.PhotoImage(pil_icon)
            self.icon_label.configure(image=self._tk_icon_ref)
        else:
            self.icon_label.configure(image="")

        self.name_label.configure(text=name or "Unknown app")

        if no_session:
            self.pct_label.configure(text="No audio session")
            self.progress["value"] = 0
        else:
            pct_int = max(0, min(100, round(pct * 100)))
            self.pct_label.configure(text=f"Volume: {pct_int}%")
            self.progress["value"] = pct_int

        self._place_window()
        self.win.deiconify()
        self.win.lift()

        if self.hide_job is not None:
            self.root.after_cancel(self.hide_job)
        self.hide_job = self.root.after(OVERLAY_DURATION_MS, self.win.withdraw)


# --------------------------- Core logic --------------------------------

def handle_app_volume(direction, overlay):
    pid, _title = get_foreground_pid_and_title()
    if pid is None:
        return

    proc_name = get_process_name(pid)
    display_name = proc_name or "Unknown"

    session = find_audio_session(pid, proc_name)

    if session is None:
        exe_path = get_exe_path(pid)
        icon = get_icon_image(exe_path)
        overlay.show(display_name, 0, icon, no_session=True)
        return

    volume_ctl = session.SimpleAudioVolume
    current = volume_ctl.GetMasterVolume()
    new_vol = max(0.0, min(1.0, current + direction * STEP))
    volume_ctl.SetMasterVolume(new_vol, None)

    exe_path = get_exe_path(pid)
    icon = get_icon_image(exe_path)

    overlay.show(display_name, new_vol, icon)


def handle_global_volume(direction, overlay):
    volume_ctl = get_master_volume_interface()
    current = volume_ctl.GetMasterVolumeLevelScalar()
    new_vol = max(0.0, min(1.0, current + direction * STEP))
    volume_ctl.SetMasterVolumeLevelScalar(new_vol, None)
    overlay.show("System Volume", new_vol, None)


def poll_queue(root, overlay):
    try:
        while True:
            mode, direction = event_queue.get_nowait()
            if mode == "app":
                handle_app_volume(direction, overlay)
            else:
                handle_global_volume(direction, overlay)
    except queue.Empty:
        pass
    root.after(30, poll_queue, root, overlay)


def main():
    root = tk.Tk()
    root.withdraw()  # hide the default root window

    overlay = Overlay(root)

    keyboard.add_hotkey(
        HOTKEY_UP, lambda: event_queue.put(("app", 1)), suppress=SUPPRESS_KEYS
    )
    keyboard.add_hotkey(
        HOTKEY_DOWN, lambda: event_queue.put(("app", -1)), suppress=SUPPRESS_KEYS
    )
    keyboard.add_hotkey(
        HOTKEY_RIGHT, lambda: event_queue.put(("global", 1)), suppress=SUPPRESS_KEYS
    )
    keyboard.add_hotkey(
        HOTKEY_LEFT, lambda: event_queue.put(("global", -1)), suppress=SUPPRESS_KEYS
    )

    root.after(30, poll_queue, root, overlay)
    root.mainloop()


if __name__ == "__main__":
    main()
