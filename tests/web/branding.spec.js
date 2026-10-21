import {test,expect} from '@playwright/test';
test('approved primary mark and transparent derivatives resolve on desktop and touch layouts',async({page,request})=>{
 await page.goto('/');const brand=page.getByRole('link',{name:'AeroScience home',exact:true}).locator('img');await expect(brand).toHaveAttribute('src','/icon.svg');
 const manifest=await(await request.get('/manifest.webmanifest')).json();expect(manifest.icons.map(i=>i.src)).toContain('/branding/primary-512.png');
 const facts=await page.evaluate(async()=>{const out=[];for(const n of [16,24,32,64,180,192,256,512]){const img=new Image();img.src=`/branding/primary-${n}.png`;await img.decode();out.push([img.naturalWidth,img.naturalHeight]);}return out;});
 expect(facts).toEqual([16,24,32,64,180,192,256,512].map(n=>[n,n]));
 await expect(brand).toBeVisible();await page.setViewportSize({width:390,height:844});await page.getByRole('button',{name:'☰ Menu',exact:true}).click();await expect(brand).toBeVisible();
 const bounds=await brand.boundingBox();expect(bounds.width).toBe(bounds.height);expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();
});
