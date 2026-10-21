// Advanced regression scenarios explicitly opt into the technical controls.
export async function openLibrary(page) {
 await page.goto('/#library/download');
 for (const title of ['Choose a specific provider', 'Advanced discovery filters', 'Advanced area selection']) {
  const details=page.locator('details').filter({has:page.locator('summary').filter({hasText:new RegExp('^'+title+'$')})}).first();
  if(!await details.evaluate(n=>n.open))await details.locator(':scope > summary').click();
 }
}
export async function applyBounds(page){await page.getByRole('button',{name:'Use bounding box',exact:true}).click();}
