// Follow visible disclosure controls for an explicitly requested advanced edit.
// Stable ancestor handles avoid re-indexing an XPath list as each disclosure opens.
// No forced clicks, DOM state changes, API shortcuts or scientific-assertion changes.
export async function revealControl(control){
 await control.waitFor({state:'attached'});
 const ownToggle=await control.evaluate(n=>n.tagName==='SUMMARY')?await control.locator('xpath=..').elementHandle():null;
 const parents=await control.locator('xpath=ancestor::details[not(@open)]').elementHandles();
 try{for(const detail of parents){if(ownToggle&&await detail.evaluate((n,own)=>n===own,ownToggle))continue;const summary=await detail.$(':scope > summary');try{await summary.click();}finally{await summary.dispose();}}}
 finally{for(const detail of parents)await detail.dispose();await ownToggle?.dispose();}
}
