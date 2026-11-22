//! Minimaler Mehrspieler-Chat als Beispiel und Test für `mett-net`.
//!
//! Über den Launcher gestartet liest er `METT_NET_*` aus der Umgebung. Zum Testen ohne Launcher:
//!   chat <addr> <token> host
//!   chat <addr> <token> join <code>
//! (Adresse und Token gibt `mett-netd` in seiner `ready`-Zeile aus.)
//! Jede eingegebene Zeile wird gesendet; der Host verteilt sie an alle.

use std::{io::BufRead, sync::mpsc, thread, time::Duration};

use mett_net::{BROADCAST, Event, HOST, LaunchAction, MettNet, Reliability};

fn main() -> std::io::Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let (mut net, action) = if args.len() >= 3 {
        let net = MettNet::connect(&args[0], &args[1], "chat")?;
        let action = match args[2].as_str() {
            "host" => LaunchAction::Host,
            "join" => LaunchAction::Join(args.get(3).cloned().unwrap_or_default()),
            _ => LaunchAction::None,
        };
        (net, action)
    } else {
        let Some(net) = MettNet::from_env()? else {
            eprintln!("Nicht über den Mett Launcher gestartet.");
            return Ok(());
        };
        let action = net.launch_action();
        (net, action)
    };

    let is_host = matches!(action, LaunchAction::Host);
    match action {
        LaunchAction::Host => net.host(8)?,
        LaunchAction::Join(code) => net.join(&code)?,
        LaunchAction::None => eprintln!("Weder hosten noch beitreten gewählt."),
    }

    let (line_tx, line_rx) = mpsc::channel::<String>();
    thread::spawn(move || {
        for line in std::io::stdin().lock().lines().map_while(Result::ok) {
            if line_tx.send(line).is_err() {
                break;
            }
        }
    });

    loop {
        for ev in net.poll() {
            match ev {
                Event::Ready { id, name } => println!("[bereit] {name} ({id})"),
                Event::Hosted { code } => println!("[host] Code: {code}"),
                Event::Joined { host_name, .. } => println!("[beigetreten] Host: {host_name}"),
                Event::PeerJoined { peer, name, .. } => println!("[+] {name} (#{peer})"),
                Event::PeerLeft { peer, reason } => println!("[-] #{peer} ({reason})"),
                Event::Message { from, data, reliability } => {
                    let text = String::from_utf8_lossy(&data);
                    println!("[#{from} {reliability:?}] {text}");
                    if is_host && reliability == Reliability::Reliable {
                        // Host verteilt Chatzeilen an alle (inkl. Absender, einfach gehalten).
                        net.send(BROADCAST, format!("#{from}: {text}").as_bytes(), Reliability::Reliable)?;
                    }
                }
                Event::SessionEnded { reason } => {
                    println!("[ende] {reason}");
                    return Ok(());
                }
                Event::Error { message } => println!("[fehler] {message}"),
                Event::Disconnected => {
                    println!("[launcher weg]");
                    return Ok(());
                }
            }
        }
        while let Ok(line) = line_rx.try_recv() {
            if line == "/quit" {
                net.leave()?;
            } else {
                net.send(if is_host { BROADCAST } else { HOST }, line.as_bytes(), Reliability::Reliable)?;
                net.send(if is_host { BROADCAST } else { HOST }, b"ping", Reliability::Unreliable)?;
            }
        }
        thread::sleep(Duration::from_millis(16));
    }
}
