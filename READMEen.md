# kicq4WP

**kicq4WP** is a native ICQ client for Windows Phone 8.1+ with support for the OSCAR protocol.

## ⚙️ Description

The client is written in C# using WinRT (Windows Runtime API). It connects directly to the KICQ server via the OSCAR protocol and implements core messaging and contact list functionality.

---

## ✅ Implemented Features

* 📡 Connection to the server `195.66.114.37:5190` using `StreamSocket`
* 🔐 DirectAuth authentication (Plain XOR)
* 🧾 Detailed logging of all connection and authentication stages (`Debug.WriteLine`)
* ✉️ Sending messages
* 📶 Status selection menu (`Online`, `Away`, `Invisible`, etc.)
* 👥 Full contact list retrieval from the server
* 👤 Contact status display (status updates may occasionally fail to appear)
* 💾 Contact list saving
* 📥 Receiving incoming messages
* 💬 Chat history display
* ↔️ Real-time status updates
* 🔔 Incoming message notifications
* 🔌 Automatic reconnection after connection loss

---

## 🚧 Work in Progress

* 🔐 MD5 authentication
* 📤 Improved SNAC message handling and server error processing
* 👥 Contact list management (adding, removing, and editing contacts)

---

## 📄 Protocol

The client implements the **OSCAR** (ICQ) protocol according to the specification, including:

* FLAP frames
* SNAC headers
* TLV structures used during authentication
* Direct authentication without a challenge request

Parts of the implementation are based on analyzing QIP network traffic with Wireshark.

---

## 📱 Compatibility

* **Platform:** Windows Phone 8.1+
* **Language:** C# / WinRT
* **IDE:** Visual Studio 2015
* **Architecture:** ARM

---

## 📝 TODO

* Improve error logging
* Verify server authentication responses (error codes and their meanings)
* Handle network timeouts
* Add visual error notifications

---

## 📷 Screenshots

Not available yet. The user interface is still under development and may change.

---

## 📌 Notes

* The OSCAR protocol has been implemented manually without using third-party libraries.
* The application has been tested on a Nokia Lumia 530 Dual SIM running Windows 10 Mobile (Version 1511).

---

## 📜 License

MIT License / Open-source project.

This project was created for educational and research purposes.
