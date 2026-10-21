import {rmSync} from 'node:fs';
export default function teardown(){rmSync('artifacts/browser-auth.json',{force:true});}
