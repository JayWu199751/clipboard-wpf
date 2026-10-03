// 参照生成器 CLI：读一个 DIB 文件，用 legacy to_png 产出 PNG，打印其 SHA-1。
// 用法：dib-reference <input.dib> <output.png>
// 本程序只在生成判据 3 参照资产时运行，不属于 C# 测试链路。

#[path = "dib.rs"]
mod dib;

use sha1::{Digest, Sha1};
use std::process::exit;

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() != 3 {
        eprintln!("用法: dib-reference <input.dib> <output.png>");
        exit(2);
    }
    let input = std::fs::read(&args[1]).unwrap_or_else(|e| {
        eprintln!("读输入失败: {e}");
        exit(1);
    });
    match dib::to_png(&input) {
        Some(png) => {
            std::fs::write(&args[2], &png).unwrap_or_else(|e| {
                eprintln!("写输出失败: {e}");
                exit(1);
            });
            let mut hasher = Sha1::new();
            hasher.update(&png);
            let digest = hex::encode(hasher.finalize());
            println!("{digest}");
        }
        None => {
            println!("NONE");
        }
    }
}
