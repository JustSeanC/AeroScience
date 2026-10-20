import {test,expect} from '@playwright/test';

test('documentation index and root installation links work offline in bundled Help',async({page,request})=>{
 await page.goto('/#help?guide=README.md');
 await expect(page.locator('.help-guide')).toContainText('AeroScience documentation');
 await page.locator('.help-guide').getByRole('link',{name:'Windows installation and onboarding',exact:true}).click();
 await expect(page).toHaveURL(/guide=_root%2FINSTALL.md/);
 await expect(page.locator('.help-guide')).toContainText('Install AeroScience for Windows');
 await page.locator('.help-guide').getByRole('link',{name:'Documentation index',exact:true}).click();
 await expect(page.locator('.help-guide')).toContainText('AeroScience documentation');
 for(const name of ['README.md','_root/INSTALL.md','_root/LICENSE.txt','roadmap/v0.5.0-development-tracker.md','research/v0.4.2-automatic-provider-acceptance.json']){
  const response=await request.get('/help/'+name);expect(response.ok(),name).toBeTruthy();
 }
});

test('Help link mapping preserves anchors and refuses executable URL schemes',async({page})=>{
 await page.goto('/#help');
 const result=await page.evaluate(async()=>{
  const {documentationUrl}=await import('/help-center.js');
  return {
   root:documentationUrl('../INSTALL.md#update-and-uninstall','README.md'),
   back:documentationUrl('docs/README.md','_root/INSTALL.md'),
   anchor:documentationUrl('#independent-storage-and-safe-maintenance','science-operations.md'),
   script:documentationUrl('javascript:alert(1)','README.md'),
   data:documentationUrl('data:text/html,unsafe','README.md')
  };
 });
 expect(result.root).toBe('#help?guide=_root%2FINSTALL.md&anchor=update-and-uninstall');
 expect(result.back).toBe('#help?guide=README.md');
 expect(result.anchor).toBe('#help?guide=science-operations.md&anchor=independent-storage-and-safe-maintenance');
 expect(result.script).toBeNull();expect(result.data).toBeNull();
});
