#pragma once
typedef struct { int state; } USBDriver;
#define USB_ACTIVE 3
extern USBDriver mock_usb;
#define USB_DRIVER mock_usb
void usbDisconnectBus(USBDriver *driver);
void restart_usb_driver(USBDriver *driver);
