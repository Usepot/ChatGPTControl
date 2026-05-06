const fs = require('fs');
const source = fs.readFileSync('desktop_state/capture-desktop-state.ps1', 'utf8');
process.stdout.write(Buffer.from(source, 'utf16le').toString('base64'));
