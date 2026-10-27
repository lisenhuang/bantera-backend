use anyhow::{Result, bail};
use candle_core::{Device, Tensor};
use candle_transformers::{models::quantized_t5 as t5, generation::LogitsProcessor};
use tokenizers::Tokenizer;
use std::io::{self, Read};
fn main() -> Result<()> {
    let root = std::env::args().nth(1).expect("model directory");
    let mut text = String::new(); io::stdin().take(8192).read_to_string(&mut text)?;
    let tokenizer = Tokenizer::from_file(format!("{root}/tokenizer.json")).map_err(anyhow::Error::msg)?;
    let tokens = tokenizer.encode(format!("<2en> {}", text.trim()), true).map_err(anyhow::Error::msg)?.get_ids().to_vec();
    if tokens.len() > 256 { bail!("input_tokens"); }
    let mut config: t5::Config = serde_json::from_str(&std::fs::read_to_string(format!("{root}/config.json"))?)?;
    config.use_cache = true;
    let device = Device::Cpu;
    let vb = t5::VarBuilder::from_gguf(format!("{root}/model-q4k.gguf"), &device)?;
    let mut model = t5::T5ForConditionalGeneration::load(vb, &config)?;
    let encoder = model.encode(&Tensor::new(tokens.as_slice(), &device)?.unsqueeze(0)?)?;
    let mut output = vec![config.decoder_start_token_id.unwrap_or(config.pad_token_id) as u32];
    let mut sampler = LogitsProcessor::new(42, None, None);
    let mut truncated = true;
    for _ in 0..128 {
        let input = Tensor::new(&[*output.last().unwrap()], &device)?.unsqueeze(0)?;
        let logits = model.decode(&input, &encoder)?.squeeze(0)?;
        let token = sampler.sample(&logits)?;
        if token as usize == config.eos_token_id { truncated = false; break; }
        output.push(token);
    }
    let result = tokenizer.decode(&output[1..], true).map_err(anyhow::Error::msg)?;
    println!("{}", serde_json::json!({"text":result,"outputTruncated":truncated}));
    Ok(())
}
