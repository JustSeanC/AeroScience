import test from 'node:test';
import assert from 'node:assert/strict';
import {preparationTransfer,networkTransfer} from '../src/AeroScience.Engine/wwwroot/transfer-progress.js';

const MiB=1048576;
function sustained(key){
 const row={key,attemptId:'first',status:'Downloading',stage:'Downloading'};
 for(const [seconds,bytes] of [[0,0],[3,3*MiB],[6,6*MiB]]){
  const result=preparationTransfer({...row,downloadedBytes:bytes,updatedUtc:new Date(seconds*1000).toISOString()},seconds*1000);
  if(seconds===6)assert.equal(result.bytesPerSecond,MiB);
 }
 return row;
}
test('P08a numeric transfer suppresses network speed during local verification and import',()=>{
 for(const stage of ['Validating','Saving']){
  const row=sustained('processing-'+stage);
  const result=preparationTransfer({...row,stage,downloadedBytes:6*MiB,updatedUtc:new Date(9000).toISOString()},9000);
  assert.equal(result.bytesPerSecond,null);assert.equal(result.remainingSecondsLow,null);assert.equal(result.remainingSecondsHigh,null);
 }
});
test('P08a a new numeric attempt does not inherit a previous attempt rate',()=>{
 const row=sustained('new-attempt');
 const result=preparationTransfer({...row,attemptId:'second',downloadedBytes:9*MiB,updatedUtc:new Date(9000).toISOString()},9000);
 assert.equal(result.bytesPerSecond,null);
});
test('P08a status updates without received bytes cannot refresh a stalled network rate',()=>{
 const row=sustained('stalled');
 preparationTransfer({...row,downloadedBytes:6*MiB,updatedUtc:new Date(9000).toISOString()},9000);
 const result=preparationTransfer({...row,downloadedBytes:6*MiB,updatedUtc:new Date(12000).toISOString()},12000);
 assert.equal(result.bytesPerSecond,null);
});
function sustainedNetwork(sessionId,activeTransfers=2){
 let result;
 for(const seconds of [0,3,6])result=networkTransfer({sessionId,bytes:seconds*MiB,elapsedSeconds:seconds,activeTransfers},seconds*1000);
 assert.equal(result.bytesPerSecond,MiB);assert.equal(result.active,activeTransfers);return result;
}
test('P08a combined rate uses received-byte deltas, not lifetime duration or transfer count',()=>{
 sustainedNetwork('two-readers');
 const result=networkTransfer({sessionId:'two-readers',bytes:9*MiB,elapsedSeconds:9,activeTransfers:2},9000);
 assert.equal(result.bytesPerSecond,MiB);assert.equal(result.bytes,9*MiB);
});
test('P08a a short burst, counter rollback and new process need new sustained measurements',()=>{
 assert.equal(networkTransfer({sessionId:'burst',bytes:0,elapsedSeconds:0,activeTransfers:1},0).bytesPerSecond,null);
 assert.equal(networkTransfer({sessionId:'burst',bytes:100*MiB,elapsedSeconds:1,activeTransfers:1},1000).bytesPerSecond,null);
 sustainedNetwork('counter');
 assert.equal(networkTransfer({sessionId:'counter',bytes:1,elapsedSeconds:9,activeTransfers:1},9000).bytesPerSecond,null);
 assert.equal(networkTransfer({sessionId:'restarted',bytes:100*MiB,elapsedSeconds:1,activeTransfers:1},9000).bytesPerSecond,null);
});
test('P08a stale snapshots and completed or locally processing transfers never retain a live rate',()=>{
 sustainedNetwork('stale');
 const stale=networkTransfer({sessionId:'stale',bytes:6*MiB,elapsedSeconds:6,activeTransfers:1},15000);
 assert.equal(stale.bytesPerSecond,null);assert.equal(stale.stale,true);
 sustainedNetwork('completed');
 assert.equal(networkTransfer({sessionId:'completed',bytes:6*MiB,elapsedSeconds:7,activeTransfers:0},7000).bytesPerSecond,null);
 for(const status of ['Ready','Offline','Unavailable']){
  const row=sustained('terminal-'+status),input={...row,status,downloadedBytes:6*MiB,updatedUtc:new Date(9000).toISOString()};
  const original=JSON.stringify(input),value=preparationTransfer(input,9000);
  assert.equal(value.bytesPerSecond,null);assert.equal(value.totalOriginalBytes,null);assert.equal(value.remainingSecondsHigh,null);assert.equal(JSON.stringify(input),original);
 }
});
