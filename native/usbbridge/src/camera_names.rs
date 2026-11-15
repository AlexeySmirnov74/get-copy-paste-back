//! Editable display-name suggestions; no hardware identities or capabilities.
use std::sync::OnceLock;

pub fn presets() -> Vec<String> {
    static NAMES: OnceLock<Vec<String>> = OnceLock::new();
    NAMES
        .get_or_init(|| {
            #[derive(serde::Deserialize)]
            struct Catalog {
                names: Vec<String>,
            }
            serde_json::from_str::<Catalog>(include_str!("../assets/camera-name-presets.json"))
                .expect("embedded camera names must be valid JSON")
                .names
        })
        .clone()
}

#[cfg(test)]
mod tests {
    #[test]
    fn camera_name_catalog_is_bounded_unique_and_valid() {
        let names = super::presets();
        assert_eq!(names.len(), 100);
        assert_eq!(
            names
                .iter()
                .map(|s| s.to_lowercase())
                .collect::<std::collections::HashSet<_>>()
                .len(),
            100
        );
        for name in names {
            assert!(!name.trim().is_empty());
            assert!(name.encode_utf16().count() <= 100);
            assert!(!name.chars().any(char::is_control));
        }
    }
}
