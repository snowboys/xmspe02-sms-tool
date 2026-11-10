# -*- coding: utf-8 -*-
"""
XMSPE02 陪伴机 AT 调试工具
功能：串口连接 -> 读取 IMEI / 信号强度 / 运营商信息
运行：python xmspe02_at_tool.py
"""

import serial
import serial.tools.list_ports
import threading
import tkinter as tk
from tkinter import ttk, messagebox, scrolledtext


class ATTool:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title("XMSPE02 陪伴机 AT 调试工具")
        self.root.geometry("640x560")
        self.root.minsize(560, 480)

        self.ser: serial.Serial | None = None
        self.polling = False

        self._build_ui()

    # ---------------- UI ----------------
    def _build_ui(self):
        # 串口设置区
        frame_conn = ttk.LabelFrame(self.root, text=" 串口连接 ", padding=8)
        frame_conn.pack(fill="x", padx=10, pady=(10, 5))

        ttk.Label(frame_conn, text="端口:").grid(row=0, column=0, padx=(0, 4))
        self.port_var = tk.StringVar()
        self.port_combo = ttk.Combobox(frame_conn, textvariable=self.port_var,
                                       width=22, state="readonly")
        self.port_combo.grid(row=0, column=1, padx=(0, 8))

        ttk.Button(frame_conn, text="刷新", width=6,
                   command=self.refresh_ports).grid(row=0, column=2, padx=(0, 12))

        ttk.Label(frame_conn, text="波特率:").grid(row=0, column=3, padx=(0, 4))
        self.baud_var = tk.StringVar(value="9600")
        ttk.Combobox(frame_conn, textvariable=self.baud_var, width=8,
                     values=["9600", "115200", "57600", "38400", "19200"],
                     state="readonly").grid(row=0, column=4, padx=(0, 12))

        self.btn_connect = ttk.Button(frame_conn, text="连接", width=8,
                                      command=self.toggle_connect)
        self.btn_connect.grid(row=0, column=5)

        # 查询按钮区
        frame_query = ttk.LabelFrame(self.root, text=" 查询功能 ", padding=8)
        frame_query.pack(fill="x", padx=10, pady=5)

        self.btn_imei = ttk.Button(frame_query, text="读取 IMEI",
                                   command=self.query_imei, state="disabled")
        self.btn_imei.pack(side="left", padx=(0, 8))

        self.btn_signal = ttk.Button(frame_query, text="查信号强度",
                                     command=self.query_signal, state="disabled")
        self.btn_signal.pack(side="left", padx=(0, 8))

        self.btn_auto = ttk.Button(frame_query, text="开始自动刷新信号",
                                   command=self.toggle_polling, state="disabled")
        self.btn_auto.pack(side="left", padx=(0, 8))

        self.btn_cops = ttk.Button(frame_query, text="查运营商",
                                   command=self.query_operator, state="disabled")
        self.btn_cops.pack(side="left", padx=(0, 8))

        self.btn_selfinfo = ttk.Button(frame_query, text="设备信息",
                                       command=self.query_device_info, state="disabled")
        self.btn_selfinfo.pack(side="left")

        # 信息显示区
        frame_info = ttk.LabelFrame(self.root, text=" 信息 ", padding=8)
        frame_info.pack(fill="both", expand=False, padx=10, pady=5)

        self.info_var = tk.StringVar(
            value="IMEI: --\n信号: --\n运营商: --")
        ttk.Label(frame_info, textvariable=self.info_var,
                  font=("Microsoft YaHei", 11), justify="left").pack(anchor="w")

        # 日志区
        frame_log = ttk.LabelFrame(self.root, text=" AT 日志 ", padding=8)
        frame_log.pack(fill="both", expand=True, padx=10, pady=(5, 10))

        self.log = scrolledtext.ScrolledText(frame_log, height=10, font=("Consolas", 9))
        self.log.pack(fill="both", expand=True)

        self.refresh_ports()

    def log_write(self, text: str):
        self.log.insert("end", text + "\n")
        self.log.see("end")

    # ---------------- 串口 ----------------
    def refresh_ports(self):
        ports = [f"{p.device}  ({p.description})" for p in serial.tools.list_ports.comports()]
        self.port_combo["values"] = ports
        if ports:
            # 优先选中包含 Quectel/2ECC 的口
            pick = next((p for p in ports if "Quectel" in p or "Mobile" in p), ports[0])
            self.port_combo.set(pick)

    def _port_name(self) -> str:
        return self.port_var.get().split("  (")[0].strip()

    def toggle_connect(self):
        if self.ser and self.ser.is_open:
            self.stop_polling()
            self.ser.close()
            self.ser = None
            self._set_state("closed")
            self.log_write("[连接] 已断开")
        else:
            try:
                self.ser = serial.Serial(self._port_name(),
                                         int(self.baud_var.get()),
                                         timeout=1)
                # 清空残留数据
                self.ser.reset_input_buffer()
                ok, _ = self.send_at("AT")
                if not ok:
                    raise RuntimeError("设备无响应 (AT 无返回 OK)")
                self._set_state("open")
                self.log_write(f"[连接] {self._port_name()} 连接成功，设备响应正常")
            except Exception as e:
                if self.ser:
                    self.ser.close()
                    self.ser = None
                messagebox.showerror("连接失败", str(e))

    def _set_state(self, state: str):
        opened = state == "open"
        for btn in (self.btn_imei, self.btn_signal, self.btn_cops, self.btn_selfinfo):
            btn["state"] = "normal" if opened else "disabled"
        self.btn_auto["state"] = "normal" if opened else "disabled"
        self.btn_connect["text"] = "断开" if opened else "连接"

    # ---------------- AT 收发 ----------------
    def send_at(self, cmd: str, timeout: float = 2.0):
        """发送 AT 指令并返回 (是否OK, 响应文本)。在工作线程调用也安全（当前为串行调用）。"""
        if not self.ser or not self.ser.is_open:
            return False, "串口未连接"
        self.ser.reset_input_buffer()
        raw = (cmd + "\r").encode("ascii")
        self.ser.write(raw)
        self.log_write(f">> {cmd}")

        buf = b""
        end = self.ser.timeout
        self.ser.timeout = 0.2
        import time
        deadline = time.time() + timeout
        while time.time() < deadline:
            chunk = self.ser.read(256)
            if chunk:
                buf += chunk
                text = buf.decode("utf-8", "ignore")
                if "OK" in text or "ERROR" in text or "+CME ERROR" in text:
                    break
        self.ser.timeout = end
        resp = buf.decode("utf-8", "ignore").strip()
        self.log_write(f"<< {resp if resp else '(无响应)'}")
        return ("OK" in resp), resp

    def _run_async(self, fn):
        threading.Thread(target=fn, daemon=True).start()

    # ---------------- 功能 ----------------
    def query_imei(self):
        self._run_async(self._do_imei)

    def _do_imei(self):
        ok, resp = self.send_at("AT+CGSN")
        imei = ""
        if ok:
            for line in resp.splitlines():
                line = line.strip().strip('"').strip()
                if line.isdigit() and len(line) == 15:
                    imei = line
                    break
        if imei:
            self._set_info("imei", imei)
            self.log_write(f"[IMEI] {imei}")
        else:
            self.log_write("[IMEI] 读取失败")

    def query_signal(self):
        self._run_async(self._do_signal)

    @staticmethod
    def csq_to_dbm(csq: int) -> str:
        if csq == 99:
            return "未知"
        return f"{-113 + csq * 2} dBm"

    def _do_signal(self):
        ok, resp = self.send_at("AT+CSQ")
        if ok:
            for line in resp.splitlines():
                if line.startswith("+CSQ:"):
                    try:
                        csq, ber = [x.strip() for x in line.split(":", 1)[1].split(",")]
                        text = f"CSQ={csq}（约 {self.csq_to_dbm(int(csq))}），误码率 BER={ber}"
                        self._set_info("signal", text)
                        self.log_write(f"[信号] {text}")
                        return
                    except Exception:
                        pass
        self.log_write("[信号] 读取失败")

    def query_operator(self):
        self._run_async(self._do_operator)

    def _do_operator(self):
        # 先检测 SIM 卡状态（该固件无卡时 COPS? 不返回运营商名）
        ok, resp = self.send_at("AT+CPIN?", timeout=3.0)
        if "ERROR" in resp and ("CME ERROR: 10" in resp or "SIM not inserted" in resp):
            self._set_info("operator", "未插卡（请插入移动 4G 卡后再试）")
            self.log_write("[运营商] 检测到 SIM 卡未插入")
            return
        if not ok:
            self._set_info("operator", "SIM 状态异常")
            self.log_write(f"[运营商] CPIN 异常: {resp}")
            return

        ok, resp = self.send_at("AT+COPS?", timeout=8.0)
        if ok:
            for line in resp.splitlines():
                if line.startswith("+COPS:"):
                    parts = [p.strip().strip('"') for p in line.split(":", 1)[1].split(",")]
                    oper = parts[2] if len(parts) >= 3 else ""
                    if oper:
                        known = {"46000": "中国移动", "46002": "中国移动", "46004": "中国移动",
                                 "46007": "中国移动", "46001": "中国联通", "46006": "中国联通",
                                 "46009": "中国联通", "46011": "中国电信", "46003": "中国电信",
                                 "46005": "中国电信", "46015": "中国广电"}
                        name = known.get(oper, f"{oper}（未知编码）")
                        self._set_info("operator", name)
                        self.log_write(f"[运营商] {name}")
                    else:
                        self._set_info("operator", "已插卡，但尚未注册上网络")
                        self.log_write("[运营商] 已插卡，未注册网络")
                    return
        self.log_write("[运营商] 读取失败")

    def query_device_info(self):
        self._run_async(self._do_device_info)

    def _do_device_info(self):
        _, resp_manu = self.send_at("AT+CGMI")
        _, resp_model = self.send_at("AT+CGMM")
        _, resp_ver = self.send_at("AT+CGMR")
        self.log_write("[设备] 厂商/型号/固件查询完成")

    def toggle_polling(self):
        if self.polling:
            self.stop_polling()
        else:
            self.polling = True
            self.btn_auto["text"] = "停止自动刷新"
            self._poll_loop()

    def stop_polling(self):
        self.polling = False
        self.btn_auto["text"] = "开始自动刷新信号"

    def _poll_loop(self):
        if not self.polling:
            return
        if self.ser and self.ser.is_open:
            self._do_signal()
            self.root.after(3000, self._poll_loop)
        else:
            self.stop_polling()

    # ---------------- 信息面板 ----------------
    def _set_info(self, key: str, value: str):
        current = {}
        for line in self.info_var.get().splitlines():
            if ":" in line:
                k, v = line.split(":", 1)
                current[k.strip()] = v.strip()
        current[key] = value
        text = (f"IMEI: {current.get('IMEI', '--')}\n"
                f"信号: {current.get('信号', '--')}\n"
                f"运营商: {current.get('运营商', '--')}")
        self.info_var.set(text)

    def on_close(self):
        self.stop_polling()
        if self.ser and self.ser.is_open:
            self.ser.close()
        self.root.destroy()


def main():
    root = tk.Tk()
    app = ATTool(root)
    root.protocol("WM_DELETE_WINDOW", app.on_close)
    root.mainloop()


if __name__ == "__main__":
    main()
