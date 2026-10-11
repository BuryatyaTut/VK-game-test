"""Offline tests with synthetic API responses; no external packages required."""

import copy
import io
import json
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest.mock import call, patch
from urllib.error import HTTPError, URLError

from main import (
    WeatherData,
    WeatherError,
    fetch_weather,
    load_cities,
    main,
    parse_weather,
    print_report,
)


def sample_payload():
    return {
        "current_condition": [{"temp_C": "18", "FeelsLikeC": "16"}],
        "nearest_area": [{"country": [{"value": "Japan"}]}],
        "weather": [{"avgtempC": "12"}],
    }


class WeatherTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.cities_path = Path(self.directory.name) / "cities.txt"

    def write_cities(self, text):
        self.cities_path.write_text(text, encoding="utf-8")
        return self.cities_path

    def run_main(self, *extra_args):
        stdout, stderr = io.StringIO(), io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            status = main([str(self.cities_path), *extra_args])
        return status, stdout.getvalue(), stderr.getvalue()

    def test_load_cities_handles_bom_blanks_case_and_duplicates(self):
        path = self.write_cities("\ufeff Moscow \n\nVienna\nmoscow\n  Москва  \nМОСКВА\n")
        self.assertEqual(load_cities(path), ["Moscow", "Vienna", "Москва"])

    def test_parses_current_temperature_and_country(self):
        self.assertEqual(parse_weather("Tokyo", sample_payload()), WeatherData("Tokyo", 18, "Japan"))

    def test_rejects_missing_and_malformed_weather(self):
        bad_payloads = [None, [], {}, {"current_condition": []}, {"current_condition": [None]}]
        for temperature in (None, True, "warm", "NaN", "Infinity", [], {}):
            payload = copy.deepcopy(sample_payload())
            payload["current_condition"][0]["temp_C"] = temperature
            bad_payloads.append(payload)
        for country in (None, 123, "", "   "):
            payload = copy.deepcopy(sample_payload())
            payload["nearest_area"][0]["country"][0]["value"] = country
            bad_payloads.append(payload)
        for payload in bad_payloads:
            with self.subTest(payload=payload), self.assertRaises(WeatherError):
                parse_weather("Tokyo", payload)

    def test_fetch_encodes_city_and_parses_json(self):
        response = io.BytesIO(json.dumps(sample_payload()).encode("utf-8"))
        with patch("main.urlopen", return_value=response) as urlopen:
            weather = fetch_weather("São Paulo/BR?", timeout=7)
        request = urlopen.call_args.args[0]
        self.assertEqual(request.full_url, "https://wttr.in/S%C3%A3o%20Paulo%2FBR%3F?format=j1")
        self.assertEqual(urlopen.call_args.kwargs, {"timeout": 7})
        self.assertEqual(weather, WeatherData("São Paulo/BR?", 18, "Japan"))

    def test_fetch_reports_http_network_and_timeout_errors(self):
        errors = [
            HTTPError("https://wttr.in/Tokyo?format=j1", 503, "Unavailable", {}, None),
            URLError("DNS failure"),
            TimeoutError("timed out"),
        ]
        for error in errors:
            with self.subTest(error=error), patch("main.urlopen", side_effect=error):
                with self.assertRaises(WeatherError):
                    fetch_weather("Tokyo")

    def test_fetch_rejects_non_json_response(self):
        with patch("main.urlopen", return_value=io.BytesIO(b"<html>Unavailable</html>")):
            with self.assertRaisesRegex(WeatherError, "not valid JSON"):
                fetch_weather("Tokyo")

    def test_report_calculates_country_statistics_and_formats_temperatures(self):
        weather = [
            WeatherData("Tokyo", 18, "Japan"),
            WeatherData("Osaka", 16, "Japan"),
            WeatherData("Kyoto", 23, "Japan"),
            WeatherData("Vienna", -3.5, "Austria"),
            WeatherData("Villach", 0, "Austria"),
            WeatherData("NhaTrang", 26, "Vietnam"),
        ]
        stdout = io.StringIO()
        with redirect_stdout(stdout):
            print_report(weather, len(weather))
        self.assertEqual(stdout.getvalue(), (
            "Current weather:\n"
            "Tokyo, Japan +18 °C\nOsaka, Japan +16 °C\nKyoto, Japan +23 °C\n"
            "Vienna, Austria -3.5 °C\nVillach, Austria +0 °C\nNhaTrang, Vietnam +26 °C\n"
            "\nBy country:\n"
            "Austria — 2 cities, avg: -1.8 °C, min: -3.5 °C, max: +0 °C\n"
            "Japan — 3 cities, avg: +19 °C, min: +16 °C, max: +23 °C\n"
            "Vietnam — 1 city, avg: +26 °C, min: +26 °C, max: +26 °C\n"
        ))

    def test_main_requests_each_unique_city_once(self):
        self.write_cities("Moscow\nVienna\nMoscow\n vienna \n")
        with patch("main.fetch_weather", side_effect=[
            WeatherData("Moscow", 10, "Russia"), WeatherData("Vienna", 14, "Austria")
        ]) as fetch:
            status, stdout, stderr = self.run_main("--timeout", "5")
        self.assertEqual(fetch.call_args_list, [call("Moscow", 5), call("Vienna", 5)])
        self.assertEqual(status, 0)
        self.assertNotIn("PARTIAL", stdout)
        self.assertEqual(stderr, "")

    def test_main_continues_after_failure_and_labels_partial_statistics(self):
        self.write_cities("Moscow\nPerm\nVienna\n")
        with patch("main.fetch_weather", side_effect=[
            WeatherError("timed out"), WeatherData("Perm", -2, "Russia"),
            WeatherData("Vienna", 10, "Austria"),
        ]) as fetch:
            status, stdout, stderr = self.run_main()
        self.assertEqual(fetch.call_count, 3)
        self.assertEqual(status, 1)
        self.assertIn("PARTIAL REPORT: 2/3 cities available", stdout)
        self.assertIn("Russia — 1 city, avg: -2 °C, min: -2 °C, max: -2 °C", stdout)
        self.assertIn("Error for Moscow: timed out", stderr)

    def test_main_handles_total_request_failure(self):
        self.write_cities("Moscow\nVienna\n")
        with patch("main.fetch_weather", side_effect=WeatherError("unavailable")):
            status, stdout, stderr = self.run_main()
        self.assertEqual(status, 1)
        self.assertEqual(stdout, "")
        self.assertIn("no statistics available", stderr)

    def test_main_handles_missing_empty_and_invalid_utf8_files(self):
        for content in (None, b" \n\n", b"\xff"):
            with self.subTest(content=content), patch("main.fetch_weather") as fetch:
                if content is not None:
                    self.cities_path.write_bytes(content)
                status, stdout, stderr = self.run_main()
                self.assertEqual(status, 1)
                self.assertEqual(stdout, "")
                self.assertIn("Error", stderr)
                fetch.assert_not_called()

    def test_rejects_invalid_timeout_before_requesting(self):
        for value in ("0", "-1", "nan", "inf", "invalid"):
            with self.subTest(value=value), patch("main.fetch_weather") as fetch:
                with self.assertRaises(SystemExit) as error:
                    self.run_main("--timeout", value)
                self.assertEqual(error.exception.code, 2)
                fetch.assert_not_called()


if __name__ == "__main__":
    unittest.main()
