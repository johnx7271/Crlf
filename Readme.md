Syntax:

CrLf fix|validate unix|windows [-f] path

fix/validate: convert/validate to Cr(ubix) or CrLf(windows)
unix/windows: Cr or CrLf
-f: optional, delete forever, else to recycle bin
path: dir or file

based on https://github.com/mfilippov/CrLfTool
auto detect encoding: use Mozilla Universal Charset Detector, see https://github.com/errepi/ude

